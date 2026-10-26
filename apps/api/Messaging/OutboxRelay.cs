using Compute.Api.Data;
using Microsoft.EntityFrameworkCore;
using RabbitMQ.Client;

namespace Compute.Api.Messaging;

/// <summary>
/// Publishes outbox rows to RabbitMQ (work queue) and Kafka (event log), then marks them sent.
/// It can publish and crash before the mark. The row is then published again, which is why every
/// consumer claims an inbox key before it acts.
/// </summary>
public sealed class OutboxRelay(
    IServiceScopeFactory scopes,
    RabbitConnection rabbit,
    KafkaLog kafka,
    ILogger<OutboxRelay> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        await kafka.EnsureTopicAsync(ct);
        var connection = await rabbit.GetAsync(ct);
        await using var channel = await connection.CreateChannelAsync(
            new CreateChannelOptions(publisherConfirmationsEnabled: true, publisherConfirmationTrackingEnabled: true),
            ct);
        await channel.ExchangeDeclareAsync(Topology.Exchange, ExchangeType.Direct, durable: true, cancellationToken: ct);

        while (!ct.IsCancellationRequested)
        {
            try
            {
                using var scope = scopes.CreateScope();
                var db = scope.ServiceProvider.GetRequiredService<ComputeDbContext>();
                var batch = await db.Outbox.Where(o => o.SentAt == null).OrderBy(o => o.Id).Take(50).ToListAsync(ct);
                if (batch.Count == 0)
                {
                    await Task.Delay(200, ct);
                    continue;
                }

                foreach (var row in batch)
                {
                    var properties = new BasicProperties
                    {
                        MessageId = row.MessageId.ToString(),
                        Type = row.RoutingKey,
                        ContentType = "application/json",
                        DeliveryMode = DeliveryModes.Persistent,
                    };
                    await channel.BasicPublishAsync(Topology.Exchange, row.RoutingKey, mandatory: false, properties,
                        System.Text.Encoding.UTF8.GetBytes(row.Payload), ct);

                    var envelope = Json.Serialize(new
                    {
                        id = row.MessageId,
                        type = row.RoutingKey,
                        module = row.Module,
                        occurredAt = row.CreatedAt,
                        payload = System.Text.Json.JsonDocument.Parse(row.Payload).RootElement,
                    });
                    await kafka.AppendAsync(row.Key, envelope, ct);

                    row.SentAt = DateTimeOffset.UtcNow;
                    await db.SaveChangesAsync(ct);
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogWarning(ex, "Outbox relay pass failed; will retry.");
                await Task.Delay(1000, ct);
            }
        }
    }
}

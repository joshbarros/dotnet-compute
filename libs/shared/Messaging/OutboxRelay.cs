using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using RabbitMQ.Client;

namespace Compute.Shared.Messaging;

public sealed record OutboxSources(IReadOnlyList<Type> ContextTypes);

/// <summary>
/// Publishes every module's outbox rows to RabbitMQ (work queue) and Kafka (event log), then marks them sent.
/// It can publish and crash before the mark. The row is then published again, which is why every consumer claims an inbox key.
/// </summary>
public sealed class OutboxRelay(
    IServiceScopeFactory scopes,
    OutboxSources sources,
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
                var published = 0;
                foreach (var contextType in sources.ContextTypes)
                {
                    using var scope = scopes.CreateScope();
                    var db = (ModuleDbContext)scope.ServiceProvider.GetRequiredService(contextType);
                    var batch = await db.Outbox.Where(o => o.SentAt == null).OrderBy(o => o.Id).Take(50).ToListAsync(ct);
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
                            Encoding.UTF8.GetBytes(row.Payload), ct);

                        var envelope = Json.Serialize(new
                        {
                            id = row.MessageId,
                            type = row.RoutingKey,
                            module = row.Module,
                            occurredAt = row.CreatedAt,
                            payload = JsonDocument.Parse(row.Payload).RootElement,
                        });
                        await kafka.AppendAsync(row.Key, envelope, ct);

                        row.SentAt = DateTimeOffset.UtcNow;
                        await db.SaveChangesAsync(ct);
                        published++;
                    }
                }

                if (published == 0)
                    await Task.Delay(200, ct);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogWarning(ex, "Outbox relay pass failed; will retry.");
                await Task.Delay(1000, ct);
            }
        }
    }
}

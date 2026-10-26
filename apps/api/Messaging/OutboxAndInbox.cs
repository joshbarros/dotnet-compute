using Compute.Api.Data;
using Microsoft.EntityFrameworkCore;

namespace Compute.Api.Messaging;

public class OutboxMessage
{
    public long Id { get; set; }
    public Guid MessageId { get; set; }
    public string Module { get; set; } = "";
    public string RoutingKey { get; set; } = "";
    public string Key { get; set; } = "";
    public string Payload { get; set; } = "";
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset? SentAt { get; set; }
}

public class InboxMessage
{
    public string Consumer { get; set; } = "";
    public Guid MessageId { get; set; }
    public DateTimeOffset ClaimedAt { get; set; }
}

public static class MessagingExtensions
{
    /// <summary>Adds an outbox row to the current unit of work. Nothing is published here.</summary>
    public static void Enqueue<T>(this ComputeDbContext db, string module, string routingKey, Guid key, T payload) =>
        db.Outbox.Add(new OutboxMessage
        {
            MessageId = Guid.CreateVersion7(),
            Module = module,
            RoutingKey = routingKey,
            Key = key.ToString(),
            Payload = Json.Serialize(payload),
            CreatedAt = DateTimeOffset.UtcNow,
        });

    /// <summary>
    /// Claims (consumer, message id). False means this consumer already handled the message.
    /// Call it inside the transaction that does the work so the claim and the effect commit together.
    /// </summary>
    public static async Task<bool> ClaimAsync(this ComputeDbContext db, string consumer, Guid messageId, CancellationToken ct) =>
        await db.Database.ExecuteSqlInterpolatedAsync(
            $"""
            INSERT INTO "InboxMessages" ("Consumer", "MessageId", "ClaimedAt")
            VALUES ({consumer}, {messageId}, {DateTimeOffset.UtcNow})
            ON CONFLICT DO NOTHING
            """, ct) == 1;
}

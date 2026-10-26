using Compute.Shared.Messaging;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace Compute.Reservations;

public enum MarkOutcome { Marked, Skipped, Duplicate }

/// <summary>Consumes the events that move a reservation forward. Each step runs only from the status it expects.</summary>
public class ReservationsHandler(ReservationsDbContext db, IOptions<ReservationOptions> options) : IMessageHandler
{
    public const string Name = "reservations";

    public Task HandleAsync(Delivery d, CancellationToken ct) => d.RoutingKey switch
    {
        Topology.HeldKey => MarkHeldAsync(d.MessageId, Json.Parse<HoursHeld>(d.Body).ReservationId, ct),
        Topology.ShortKey => MarkShortAsync(d.MessageId, Json.Parse<HoursShort>(d.Body), ct),
        Topology.IssuedKey => MarkLeasedAsync(d.MessageId, Json.Parse<LeaseIssued>(d.Body), ct),
        Topology.RejectedKey => MarkRejectedAsync(d.MessageId, Json.Parse<LeaseRejected>(d.Body), ct),
        Topology.ReleasedKey => MarkReleasedAsync(d.MessageId, Json.Parse<HoursReleased>(d.Body), ct),
        _ => throw new InvalidOperationException($"Reservations does not handle '{d.RoutingKey}'."),
    };

    public Task<MarkOutcome> MarkHeldAsync(Guid messageId, Guid reservationId, CancellationToken ct = default) =>
        StepAsync(messageId, ct, () => db.Reservations
            .Where(b => b.Id == reservationId && b.Status == ReservationStatus.Requested)
            .ExecuteUpdateAsync(s => s
                .SetProperty(b => b.Status, ReservationStatus.Held)
                .SetProperty(b => b.UpdatedAt, DateTimeOffset.UtcNow), ct));

    public Task<MarkOutcome> MarkShortAsync(Guid messageId, HoursShort m, CancellationToken ct = default) =>
        StepAsync(messageId, ct, () => db.Reservations
            .Where(b => b.Id == m.ReservationId && b.Status == ReservationStatus.Requested)
            .ExecuteUpdateAsync(s => s
                .SetProperty(b => b.Status, ReservationStatus.Rejected)
                .SetProperty(b => b.Reason, $"Short: {m.RequestedHours} hours requested, {m.HoursLeft} remaining.")
                .SetProperty(b => b.UpdatedAt, DateTimeOffset.UtcNow), ct));

    /// <summary>The final mark. If a cancellation got there first the row is no longer Held and nothing changes.</summary>
    public async Task<MarkOutcome> MarkLeasedAsync(Guid messageId, LeaseIssued m, CancellationToken ct = default)
    {
        if (options.Value.FinalMarkDelayMs > 0)
            await Task.Delay(options.Value.FinalMarkDelayMs, ct);

        return await StepAsync(messageId, ct, () => db.Reservations
            .Where(b => b.Id == m.ReservationId && b.Status == ReservationStatus.Held)
            .ExecuteUpdateAsync(s => s
                .SetProperty(b => b.Status, ReservationStatus.Leased)
                .SetProperty(b => b.LeaseNumber, m.Number)
                .SetProperty(b => b.UpdatedAt, DateTimeOffset.UtcNow), ct),
            reservationIdForOrderCheck: m.ReservationId);
    }

    public Task<MarkOutcome> MarkRejectedAsync(Guid messageId, LeaseRejected m, CancellationToken ct = default) =>
        StepAsync(messageId, ct, () => db.Reservations
            .Where(b => b.Id == m.ReservationId && (b.Status == ReservationStatus.Held || b.Status == ReservationStatus.Requested))
            .ExecuteUpdateAsync(s => s
                .SetProperty(b => b.Status, ReservationStatus.Rejected)
                .SetProperty(b => b.Reason, m.Reason)
                .SetProperty(b => b.UpdatedAt, DateTimeOffset.UtcNow), ct));

    public Task<MarkOutcome> MarkReleasedAsync(Guid messageId, HoursReleased m, CancellationToken ct = default) =>
        StepAsync(messageId, ct, () => db.Reservations
            .Where(b => b.Id == m.ReservationId && b.HoursReleasedAt == null)
            .ExecuteUpdateAsync(s => s
                .SetProperty(b => b.HoursReleasedAt, DateTimeOffset.UtcNow), ct));

    private async Task<MarkOutcome> StepAsync(Guid messageId, CancellationToken ct, Func<Task<int>> update, Guid? reservationIdForOrderCheck = null)
    {
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        if (!await db.ClaimAsync(Name, messageId, ct))
        {
            await tx.CommitAsync(ct);
            return MarkOutcome.Duplicate;
        }

        var changed = await update();
        if (changed == 0 && reservationIdForOrderCheck is { } id)
        {
            // Skipped for a reason we can name? Requested means the "Held" mark has not landed yet: retry, do not drop.
            var status = await db.Reservations.AsNoTracking().Where(b => b.Id == id).Select(b => b.Status).FirstOrDefaultAsync(ct);
            if (status == ReservationStatus.Requested)
                throw new OutOfOrderException("Final mark arrived before the Held mark.");
        }

        await tx.CommitAsync(ct);
        return changed == 1 ? MarkOutcome.Marked : MarkOutcome.Skipped;
    }
}

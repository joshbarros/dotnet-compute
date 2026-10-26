using Compute.Shared.Messaging;
using Microsoft.EntityFrameworkCore;

namespace Compute.Pools;

public enum ReserveOutcome { Held, Short, Duplicate }

/// <summary>
/// The only writer of Pool.HoursLeft and of hour holds.
/// The reservation is one conditional UPDATE: it matches only while the remaining Hours still covers the request.
/// </summary>
public class PoolsHandler(PoolsDbContext db) : IMessageHandler
{
    public const string Name = "pools";

    public Task HandleAsync(Delivery delivery, CancellationToken ct) => delivery.RoutingKey switch
    {
        Topology.ReserveKey => ReserveAsync(delivery.MessageId, Json.Parse<ReserveHours>(delivery.Body), ct),
        Topology.ReleaseKey => ReleaseAsync(delivery.MessageId, Json.Parse<ReleaseHours>(delivery.Body), ct),
        _ => throw new InvalidOperationException($"Pools does not handle '{delivery.RoutingKey}'."),
    };

    public async Task<ReserveOutcome> ReserveAsync(Guid messageId, ReserveHours command, CancellationToken ct = default)
    {
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        if (!await db.ClaimAsync(Name, messageId, ct))
        {
            await tx.CommitAsync(ct);
            return ReserveOutcome.Duplicate;
        }

        var updated = await db.Pools
            .Where(s => s.Id == command.PoolId && s.HoursLeft >= command.Hours)
            .ExecuteUpdateAsync(s => s.SetProperty(x => x.HoursLeft, x => x.HoursLeft - command.Hours), ct);

        ReserveOutcome outcome;
        if (updated == 1)
        {
            db.Holds.Add(new HourHold
            {
                ReservationId = command.ReservationId,
                PoolId = command.PoolId,
                Hours = command.Hours,
                Status = HoldStatus.Held,
                CreatedAt = DateTimeOffset.UtcNow,
            });
            db.Enqueue("pools", Topology.HeldKey, command.ReservationId,
                new HoursHeld(command.ReservationId, command.OwnerId, command.PoolId, command.Hours, command.Reference));
            outcome = ReserveOutcome.Held;
        }
        else
        {
            var remaining = await db.Pools.Where(s => s.Id == command.PoolId)
                .Select(s => s.HoursLeft).FirstOrDefaultAsync(ct);
            db.Enqueue("pools", Topology.ShortKey, command.ReservationId,
                new HoursShort(command.ReservationId, command.Reference, command.Hours, remaining));
            outcome = ReserveOutcome.Short;
        }

        await db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);
        return outcome;
    }

    /// <summary>Gives held hours back exactly once. A second release for the same reservation changes nothing.</summary>
    public async Task<bool> ReleaseAsync(Guid messageId, ReleaseHours command, CancellationToken ct = default)
    {
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        if (!await db.ClaimAsync(Name, messageId, ct))
        {
            await tx.CommitAsync(ct);
            return false;
        }

        var hold = await db.Holds.AsNoTracking().FirstOrDefaultAsync(h => h.ReservationId == command.ReservationId, ct);
        var released = false;
        if (hold is not null)
        {
            var now = DateTimeOffset.UtcNow;
            var flipped = await db.Holds
                .Where(h => h.ReservationId == command.ReservationId && h.Status == HoldStatus.Held)
                .ExecuteUpdateAsync(s => s
                    .SetProperty(h => h.Status, HoldStatus.Released)
                    .SetProperty(h => h.ReleasedAt, now)
                    .SetProperty(h => h.ReleaseReason, command.Reason), ct);
            if (flipped == 1)
            {
                await db.Pools
                    .Where(s => s.Id == hold.PoolId && s.HoursLeft + hold.Hours <= s.HourCapacity)
                    .ExecuteUpdateAsync(s => s.SetProperty(x => x.HoursLeft, x => x.HoursLeft + hold.Hours), ct);
                db.Enqueue("pools", Topology.ReleasedKey, command.ReservationId,
                    new HoursReleased(command.ReservationId, hold.Hours, command.Reason));
                released = true;
            }
        }

        await db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);
        return released;
    }
}

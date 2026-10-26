using Compute.Api.Data;
using Compute.Api.Messaging;
using Compute.Api.Modules.Pools;
using Compute.Api.Modules.Leases;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Npgsql;

namespace Compute.Api.Modules.Reservations;

public enum ReservationStatus { Requested, Held, Leased, Cancelled, Rejected }

public class Reservation
{
    public Guid Id { get; set; }
    public Guid PoolId { get; set; }
    public string PoolCode { get; set; } = "";
    public string Reference { get; set; } = "";
    public int Hours { get; set; }
    public long FeeCents { get; set; }
    public ReservationStatus Status { get; set; }
    public string? Reason { get; set; }
    public string? LeaseNumber { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
    public DateTimeOffset? HoursReleasedAt { get; set; }
}

public class ReservationOptions
{
    /// <summary>Latency knob for the final mark. Compose sets it so the cancellation race window is wide enough to hit on purpose.</summary>
    public int FinalMarkDelayMs { get; set; }
}

public class DomainException(int status, string message) : Exception(message)
{
    public int Status { get; } = status;
}

public record CreateReservationRequest(string? PoolCode, string? Reference, long Hours);

public record ReservationDto(
    Guid Id, string PoolCode, string Reference, int Hours, long FeeCents, string Status,
    string? Reason, string? LeaseNumber, DateTimeOffset CreatedAt, DateTimeOffset UpdatedAt,
    DateTimeOffset? HoursReleasedAt)
{
    public static ReservationDto From(Reservation b) => new(
        b.Id, b.PoolCode, b.Reference, b.Hours, b.FeeCents, b.Status.ToString(),
        b.Reason, b.LeaseNumber, b.CreatedAt, b.UpdatedAt, b.HoursReleasedAt);
}

/// <summary>HTTP side of the saga: writes the reservation and its first outbox row in one local transaction.</summary>
public class ReservationService(ComputeDbContext db)
{
    public async Task<Reservation> CreateAsync(CreateReservationRequest request, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(request.PoolCode))
            throw new DomainException(400, "Pool code is required.");
        var error = ReservationRules.Validate(request.Reference, request.Hours);
        if (error is not null)
            throw new DomainException(400, error);

        var pool = await db.Pools.AsNoTracking().FirstOrDefaultAsync(s => s.Code == request.PoolCode.Trim(), ct)
            ?? throw new DomainException(404, $"Pool '{request.PoolCode}' does not exist.");

        var hours = (int)request.Hours;
        var reference = request.Reference!.Trim();
        var now = DateTimeOffset.UtcNow;
        var reservation = new Reservation
        {
            Id = Guid.CreateVersion7(),
            PoolId = pool.Id,
            PoolCode = pool.Code,
            Reference = reference,
            Hours = hours,
            FeeCents = ReservationRules.FeeCents(hours),
            Status = ReservationStatus.Requested,
            CreatedAt = now,
            UpdatedAt = now,
        };

        await using var tx = await db.Database.BeginTransactionAsync(ct);
        db.Reservations.Add(reservation);
        db.Enqueue("reservations", Topology.ReserveKey, reservation.Id, new ReserveHours(reservation.Id, pool.Id, hours, reference));
        try
        {
            await db.SaveChangesAsync(ct);
        }
        catch (DbUpdateException ex) when (ex.InnerException is PostgresException { SqlState: PostgresErrorCodes.UniqueViolation })
        {
            throw new DomainException(409, $"Reference '{reference}' is already booked on {pool.Code}.");
        }
        await tx.CommitAsync(ct);
        return reservation;
    }

    /// <summary>Moves Held or Leased to Cancelled and asks Pools to give the hours back. The lease is not touched.</summary>
    public async Task<Reservation> CancelAsync(Guid id, CancellationToken ct)
    {
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        var now = DateTimeOffset.UtcNow;
        var moved = await db.Reservations
            .Where(b => b.Id == id && (b.Status == ReservationStatus.Held || b.Status == ReservationStatus.Leased))
            .ExecuteUpdateAsync(s => s
                .SetProperty(b => b.Status, ReservationStatus.Cancelled)
                .SetProperty(b => b.Reason, "Cancelled by request.")
                .SetProperty(b => b.UpdatedAt, now), ct);
        if (moved != 1)
        {
            var current = await db.Reservations.AsNoTracking().FirstOrDefaultAsync(b => b.Id == id, ct)
                ?? throw new DomainException(404, "Reservation not found.");
            throw new DomainException(409, $"A reservation in status {current.Status} cannot be cancelled.");
        }

        db.Enqueue("reservations", Topology.ReleaseKey, id, new ReleaseHours(id, "cancellation"));
        await db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);
        return await db.Reservations.AsNoTracking().FirstAsync(b => b.Id == id, ct);
    }
}

public enum MarkOutcome { Marked, Skipped, Duplicate }

/// <summary>Consumes the events that move a reservation forward. Each step runs only from the status it expects.</summary>
public class ReservationsHandler(ComputeDbContext db, IOptions<ReservationOptions> options) : IMessageHandler
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

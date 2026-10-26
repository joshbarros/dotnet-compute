using Compute.Shared;
using Compute.Shared.Auth;
using Compute.Shared.Contracts;
using Compute.Shared.Messaging;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace Compute.Reservations;

/// <summary>HTTP side of the saga: writes the reservation and its first outbox row in one local transaction.</summary>
public class ReservationService(ReservationsDbContext db, IPoolCatalog pools, ICurrentUser user)
{
    public async Task<Reservation> CreateAsync(CreateReservationRequest request, CancellationToken ct)
    {
        var ownerId = user.Id ?? throw new DomainException(401, "Sign in to book.");
        if (string.IsNullOrWhiteSpace(request.PoolCode))
            throw new DomainException(400, "Pool code is required.");
        var error = ReservationRules.Validate(request.Reference, request.Hours);
        if (error is not null)
            throw new DomainException(400, error);

        var pool = await pools.FindAsync(request.PoolCode.Trim(), ct)
            ?? throw new DomainException(404, $"Pool '{request.PoolCode}' does not exist.");

        var hours = (int)request.Hours;
        var reference = request.Reference!.Trim();
        var now = DateTimeOffset.UtcNow;
        var reservation = new Reservation
        {
            Id = Guid.CreateVersion7(),
            OwnerId = ownerId,
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
        db.Enqueue("reservations", Topology.ReserveKey, reservation.Id, new ReserveHours(reservation.Id, ownerId, pool.Id, hours, reference));
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

    /// <summary>The owner or staff only. Anyone else gets a 404, so ids cannot be probed.</summary>
    public async Task<Reservation> GetAsync(Guid id, CancellationToken ct)
    {
        var reservation = await db.Reservations.AsNoTracking().FirstOrDefaultAsync(b => b.Id == id, ct);
        if (reservation is null || !OwnerAccess.CanAccess(user, reservation.OwnerId))
            throw new DomainException(404, "Reservation not found.");
        return reservation;
    }

    public async Task<List<Reservation>> ListAsync(string? poolCode, CancellationToken ct)
    {
        var query = db.Reservations.AsNoTracking();
        if (!user.IsStaff)
        {
            var me = user.Id ?? throw new DomainException(401, "Sign in to see reservations.");
            query = query.Where(b => b.OwnerId == me);
        }
        if (!string.IsNullOrWhiteSpace(poolCode))
            query = query.Where(b => b.PoolCode == poolCode);
        return await query.OrderByDescending(b => b.CreatedAt).Take(100).ToListAsync(ct);
    }

    /// <summary>Moves Held or Leased to Cancelled and asks Pools to give the hours back. The lease is not touched.</summary>
    public async Task<Reservation> CancelAsync(Guid id, CancellationToken ct)
    {
        await GetAsync(id, ct); // 404 unless the caller may see it

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
            var current = await db.Reservations.AsNoTracking().FirstAsync(b => b.Id == id, ct);
            throw new DomainException(409, $"A reservation in status {current.Status} cannot be cancelled.");
        }

        db.Enqueue("reservations", Topology.ReleaseKey, id, new ReleaseHours(id, "cancellation"));
        await db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);
        return await db.Reservations.AsNoTracking().FirstAsync(b => b.Id == id, ct);
    }
}

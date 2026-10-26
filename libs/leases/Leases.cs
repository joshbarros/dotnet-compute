using Compute.Shared;
using Compute.Shared.Messaging;
using Microsoft.EntityFrameworkCore;

namespace Compute.Leases;

public enum LeaseStatus { Issued, Rejected }

public class Lease
{
    public Guid ReservationId { get; set; }
    public Guid OwnerId { get; set; }
    public string? Number { get; set; }
    public LeaseStatus Status { get; set; }
    public string? Reason { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
}

public class LeasesDbContext(DbContextOptions<LeasesDbContext> options) : ModuleDbContext(options, SchemaName)
{
    public const string SchemaName = "leases";

    public DbSet<Lease> Leases => Set<Lease>();

    protected override void OnModelCreating(ModelBuilder b)
    {
        base.OnModelCreating(b);

        b.Entity<Lease>(e =>
        {
            e.ToTable("Leases");
            e.HasKey(x => x.ReservationId);
            e.Property(x => x.Status).HasConversion<string>().HasMaxLength(16);
        });
    }
}

public enum IssueOutcome { Issued, Rejected, Duplicate }

/// <summary>Issues the lease once the hours are held. A poison reference is committed as rejected.</summary>
public class LeasesHandler(LeasesDbContext db) : IMessageHandler
{
    public const string Name = "leases";

    public async Task HandleAsync(Delivery delivery, CancellationToken ct)
    {
        if (delivery.RoutingKey != Topology.HeldKey)
            throw new InvalidOperationException($"Leases does not handle '{delivery.RoutingKey}'.");

        var outcome = await IssueAsync(delivery.MessageId, Json.Parse<HoursHeld>(delivery.Body), ct);
        if (outcome == IssueOutcome.Rejected)
            throw new PoisonMessageException("Reference carries the poison token; rejection is committed.");
    }

    public async Task<IssueOutcome> IssueAsync(Guid messageId, HoursHeld held, CancellationToken ct = default)
    {
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        if (!await db.ClaimAsync(Name, messageId, ct))
        {
            await tx.CommitAsync(ct);
            return IssueOutcome.Duplicate;
        }

        IssueOutcome outcome;
        if (PoisonRule.IsPoison(held.Reference))
        {
            const string reason = "Reference contains the poison token.";
            db.Leases.Add(new Lease
            {
                ReservationId = held.ReservationId,
                OwnerId = held.OwnerId,
                Status = LeaseStatus.Rejected,
                Reason = reason,
                CreatedAt = DateTimeOffset.UtcNow,
            });
            db.Enqueue("leases", Topology.ReleaseKey, held.ReservationId, new ReleaseHours(held.ReservationId, "poison"));
            db.Enqueue("leases", Topology.RejectedKey, held.ReservationId, new LeaseRejected(held.ReservationId, reason));
            outcome = IssueOutcome.Rejected;
        }
        else
        {
            var number = $"LS-{held.ReservationId.ToString("N")[^12..].ToUpperInvariant()}";
            db.Leases.Add(new Lease
            {
                ReservationId = held.ReservationId,
                OwnerId = held.OwnerId,
                Number = number,
                Status = LeaseStatus.Issued,
                CreatedAt = DateTimeOffset.UtcNow,
            });
            db.Enqueue("leases", Topology.IssuedKey, held.ReservationId, new LeaseIssued(held.ReservationId, number));
            outcome = IssueOutcome.Issued;
        }

        await db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);
        return outcome;
    }
}

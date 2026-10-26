using Compute.Api.Messaging;
using Compute.Api.Modules.Reservations;
using Compute.Api.Modules.Pools;
using Compute.Api.Modules.Leases;
using Microsoft.EntityFrameworkCore;

namespace Compute.Api.Data;

public class ComputeDbContext(DbContextOptions<ComputeDbContext> options) : DbContext(options)
{
    public DbSet<Pool> Pools => Set<Pool>();
    public DbSet<HourHold> Holds => Set<HourHold>();
    public DbSet<Reservation> Reservations => Set<Reservation>();
    public DbSet<Lease> Leases => Set<Lease>();
    public DbSet<OutboxMessage> Outbox => Set<OutboxMessage>();
    public DbSet<InboxMessage> Inbox => Set<InboxMessage>();

    protected override void OnModelCreating(ModelBuilder b)
    {
        b.Entity<Pool>(e =>
        {
            e.HasKey(x => x.Id);
            e.HasIndex(x => x.Code).IsUnique();
            e.Property(x => x.Code).HasMaxLength(20);
            e.ToTable(t => t.HasCheckConstraint("CK_Pools_Remaining", "\"HoursLeft\" >= 0 AND \"HoursLeft\" <= \"HourCapacity\""));
        });

        b.Entity<HourHold>(e =>
        {
            e.ToTable("HourHolds");
            e.HasKey(x => x.ReservationId);
            e.Property(x => x.Status).HasConversion<string>().HasMaxLength(16);
            e.HasOne<Pool>().WithMany().HasForeignKey(x => x.PoolId);
        });

        b.Entity<Reservation>(e =>
        {
            e.HasKey(x => x.Id);
            e.Property(x => x.Status).HasConversion<string>().HasMaxLength(16);
            e.Property(x => x.Reference).HasMaxLength(ReservationRules.MaxReferenceLength);
            e.HasIndex(x => new { x.PoolId, x.Reference }).IsUnique();
            e.HasOne<Pool>().WithMany().HasForeignKey(x => x.PoolId);
        });

        b.Entity<Lease>(e =>
        {
            e.ToTable("Leases");
            e.HasKey(x => x.ReservationId);
            e.Property(x => x.Status).HasConversion<string>().HasMaxLength(16);
        });

        b.Entity<OutboxMessage>(e =>
        {
            e.ToTable("OutboxMessages");
            e.HasKey(x => x.Id);
            e.Property(x => x.Id).UseIdentityAlwaysColumn();
            e.Property(x => x.Payload).HasColumnType("jsonb");
            e.HasIndex(x => x.SentAt).HasFilter("\"SentAt\" IS NULL");
        });

        b.Entity<InboxMessage>(e =>
        {
            e.ToTable("InboxMessages");
            e.HasKey(x => new { x.Consumer, x.MessageId });
        });
    }
}

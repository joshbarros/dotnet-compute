using Compute.Shared.Messaging;
using Microsoft.EntityFrameworkCore;

namespace Compute.Pools;

public class PoolsDbContext(DbContextOptions<PoolsDbContext> options) : ModuleDbContext(options, SchemaName)
{
    public const string SchemaName = "hours";

    public DbSet<Pool> Pools => Set<Pool>();
    public DbSet<HourHold> Holds => Set<HourHold>();

    protected override void OnModelCreating(ModelBuilder b)
    {
        base.OnModelCreating(b);

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
    }
}

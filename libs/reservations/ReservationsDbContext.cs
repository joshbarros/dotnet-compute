using Compute.Shared.Messaging;
using Microsoft.EntityFrameworkCore;

namespace Compute.Reservations;

public class ReservationsDbContext(DbContextOptions<ReservationsDbContext> options) : ModuleDbContext(options, SchemaName)
{
    public const string SchemaName = "reservations";

    public DbSet<Reservation> Reservations => Set<Reservation>();

    protected override void OnModelCreating(ModelBuilder b)
    {
        base.OnModelCreating(b);

        b.Entity<Reservation>(e =>
        {
            e.HasKey(x => x.Id);
            e.Property(x => x.Status).HasConversion<string>().HasMaxLength(16);
            e.Property(x => x.Reference).HasMaxLength(ReservationRules.MaxReferenceLength);
            e.HasIndex(x => new { x.PoolId, x.Reference }).IsUnique();
            e.HasIndex(x => x.OwnerId);
        });
    }
}

using Compute.Api.Data;
using Compute.Api.Modules.Reservations;
using Compute.Api.Modules.Pools;
using Microsoft.EntityFrameworkCore;

namespace Compute.Api.Tests;

public static class TestDatabase
{
    public static readonly string ConnectionString =
        Environment.GetEnvironmentVariable("COMPUTE_TEST_DB")
        ?? "Host=localhost;Port=5475;Database=compute_test;Username=compute;Password=compute";

    private static readonly DbContextOptions<ComputeDbContext> Options =
        new DbContextOptionsBuilder<ComputeDbContext>().UseNpgsql(ConnectionString).Options;

    public static ComputeDbContext Create() => new(Options);

    public static async Task<ComputeDbContext> OpenAsync()
    {
        var db = Create();
        await db.Database.MigrateAsync();
        await db.Database.ExecuteSqlRawAsync(
            "TRUNCATE \"InboxMessages\", \"OutboxMessages\", \"Leases\", \"HourHolds\", \"Reservations\", \"Pools\" RESTART IDENTITY CASCADE;");
        return db;
    }

    public static async Task<Pool> AddPoolAsync(ComputeDbContext db, int hourCapacity)
    {
        var pool = new Pool
        {
            Id = Guid.CreateVersion7(),
            Code = $"T-{Guid.NewGuid():N}"[..14],
            Name = "MV Test",
            GpuModel = "H100",
            Region = "us-east1",
            Day = DateTimeOffset.UtcNow.AddDays(7),
            HourCapacity = hourCapacity,
            HoursLeft = hourCapacity,
            CreatedAt = DateTimeOffset.UtcNow,
        };
        db.Pools.Add(pool);
        await db.SaveChangesAsync();
        return pool;
    }

    public static async Task<Reservation> AddReservationAsync(ComputeDbContext db, Pool pool, string reference, int hours, ReservationStatus status)
    {
        var reservation = new Reservation
        {
            Id = Guid.CreateVersion7(),
            PoolId = pool.Id,
            PoolCode = pool.Code,
            Reference = reference,
            Hours = hours,
            FeeCents = ReservationRules.FeeCents(hours),
            Status = status,
            CreatedAt = DateTimeOffset.UtcNow,
            UpdatedAt = DateTimeOffset.UtcNow,
        };
        db.Reservations.Add(reservation);
        await db.SaveChangesAsync();
        return reservation;
    }
}

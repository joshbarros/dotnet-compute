using Compute.Reservations;
using Compute.Pools;
using Compute.Leases;
using Compute.Shared.Auth;
using Compute.Shared.Contracts;
using Microsoft.EntityFrameworkCore;

namespace Compute.IntegrationTests;

public static class TestDatabase
{
    public static readonly string ConnectionString =
        Environment.GetEnvironmentVariable("COMPUTE_TEST_DB")
        ?? "Host=localhost;Port=5475;Database=compute_test;Username=compute;Password=compute";

    private static DbContextOptions<T> Options<T>(string schema) where T : DbContext =>
        new DbContextOptionsBuilder<T>()
            .UseNpgsql(ConnectionString, n => n.MigrationsHistoryTable("__EFMigrationsHistory", schema))
            .Options;

    public static ReservationsDbContext Reservations() => new(Options<ReservationsDbContext>(ReservationsDbContext.SchemaName));
    public static PoolsDbContext Pools() => new(Options<PoolsDbContext>(PoolsDbContext.SchemaName));
    public static LeasesDbContext Leases() => new(Options<LeasesDbContext>(LeasesDbContext.SchemaName));

    /// <summary>Migrates the business modules and empties their tables. The identity schema is left alone: the API tests share it.</summary>
    public static async Task OpenAsync()
    {
        await using var reservations = Reservations();
        await using var hours = Pools();
        await using var leases = Leases();
        await reservations.Database.MigrateAsync();
        await hours.Database.MigrateAsync();
        await leases.Database.MigrateAsync();
        await reservations.Database.ExecuteSqlRawAsync(
            "TRUNCATE reservations.\"InboxMessages\", reservations.\"OutboxMessages\", reservations.\"Reservations\", " +
            "hours.\"InboxMessages\", hours.\"OutboxMessages\", hours.\"HourHolds\", hours.\"Pools\", " +
            "leases.\"InboxMessages\", leases.\"OutboxMessages\", leases.\"Leases\" RESTART IDENTITY CASCADE;");
    }

    public static async Task<Pool> AddPoolAsync(int hourCapacity)
    {
        await using var db = Pools();
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

    public static async Task<Reservation> AddReservationAsync(Pool pool, string reference, int hours, ReservationStatus status, Guid? ownerId = null)
    {
        await using var db = Reservations();
        var reservation = new Reservation
        {
            Id = Guid.CreateVersion7(),
            OwnerId = ownerId ?? Guid.CreateVersion7(),
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

public sealed class TestUser(Guid? id, bool staff = false) : ICurrentUser
{
    public bool IsAuthenticated => id is not null;
    public Guid? Id => id;
    public bool IsStaff => staff;
}

public sealed class FixedCatalog(params Pool[] pools) : IPoolCatalog
{
    public Task<PoolInfo?> FindAsync(string code, CancellationToken ct) =>
        Task.FromResult(pools.Where(s => s.Code == code).Select(s => new PoolInfo(s.Id, s.Code)).FirstOrDefault());
}

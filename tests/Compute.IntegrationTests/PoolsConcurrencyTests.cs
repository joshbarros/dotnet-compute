using System.Collections.Concurrent;
using Compute.Pools;
using Compute.Shared.Messaging;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace Compute.IntegrationTests;

public class PoolsConcurrencyTests
{
    [Fact]
    public async Task LastHour_TwoConcurrentReservations_OneHeldOneShortNoHoldForTheLoser()
    {
        await TestDatabase.OpenAsync();
        var pool = await TestDatabase.AddPoolAsync(hourCapacity: 1);

        var outcomes = new ConcurrentBag<(Guid Reservation, ReserveOutcome Outcome)>();
        var gate = new TaskCompletionSource();
        var tasks = Enumerable.Range(0, 2).Select(n => Task.Run(async () =>
        {
            await gate.Task;
            var reservationId = Guid.CreateVersion7();
            await using var db = TestDatabase.Pools();
            var outcome = await new PoolsHandler(db).ReserveAsync(
                Guid.CreateVersion7(), new ReserveHours(reservationId, Guid.NewGuid(), pool.Id, 1, $"TEAM-{n}"));
            outcomes.Add((reservationId, outcome));
        })).ToArray();
        gate.SetResult();
        await Task.WhenAll(tasks);

        await using var check = TestDatabase.Pools();
        var remaining = await check.Pools.Where(s => s.Id == pool.Id).Select(s => s.HoursLeft).FirstAsync();
        var holds = await check.Holds.ToListAsync();
        var loser = outcomes.Single(o => o.Outcome == ReserveOutcome.Short).Reservation;

        Assert.Single(outcomes, o => o.Outcome == ReserveOutcome.Held);
        Assert.Single(outcomes, o => o.Outcome == ReserveOutcome.Short);
        Assert.Equal(0, remaining);
        Assert.Single(holds);
        Assert.DoesNotContain(holds, h => h.ReservationId == loser);
        Assert.Equal(1, await check.Outbox.CountAsync(o => o.RoutingKey == Topology.HeldKey));
        Assert.Equal(1, await check.Outbox.CountAsync(o => o.RoutingKey == Topology.ShortKey));
    }

    [Fact]
    public async Task Release_GivesTheHoursBackExactlyOnce()
    {
        await TestDatabase.OpenAsync();
        var pool = await TestDatabase.AddPoolAsync(hourCapacity: 4);
        var reservationId = Guid.CreateVersion7();
        await using (var db = TestDatabase.Pools())
            await new PoolsHandler(db).ReserveAsync(Guid.CreateVersion7(), new ReserveHours(reservationId, Guid.NewGuid(), pool.Id, 3, "TEAM-1"));

        // Two different commands (poison compensation and a cancellation) both ask for the same hours back.
        await using var first = TestDatabase.Pools();
        await using var second = TestDatabase.Pools();
        var one = await new PoolsHandler(first).ReleaseAsync(Guid.CreateVersion7(), new ReleaseHours(reservationId, "poison"));
        var two = await new PoolsHandler(second).ReleaseAsync(Guid.CreateVersion7(), new ReleaseHours(reservationId, "cancellation"));

        await using var check = TestDatabase.Pools();
        Assert.True(one);
        Assert.False(two);
        Assert.Equal(4, await check.Pools.Where(s => s.Id == pool.Id).Select(s => s.HoursLeft).FirstAsync());
    }

    [Fact]
    public async Task SameMessageDeliveredTwice_IsClaimedOnce()
    {
        await TestDatabase.OpenAsync();
        var pool = await TestDatabase.AddPoolAsync(hourCapacity: 5);
        var messageId = Guid.CreateVersion7();
        var command = new ReserveHours(Guid.CreateVersion7(), Guid.NewGuid(), pool.Id, 2, "TEAM-1");

        await using var a = TestDatabase.Pools();
        await using var b = TestDatabase.Pools();
        var first = await new PoolsHandler(a).ReserveAsync(messageId, command);
        var second = await new PoolsHandler(b).ReserveAsync(messageId, command);

        await using var check = TestDatabase.Pools();
        Assert.Equal(ReserveOutcome.Held, first);
        Assert.Equal(ReserveOutcome.Duplicate, second);
        Assert.Equal(3, await check.Pools.Where(s => s.Id == pool.Id).Select(s => s.HoursLeft).FirstAsync());
    }

    [Fact]
    public async Task TheDatabaseItself_RefusesANegativeRemainder()
    {
        await TestDatabase.OpenAsync();
        var pool = await TestDatabase.AddPoolAsync(hourCapacity: 2);
        await using var db = TestDatabase.Pools();

        // A bug that skipped the WHERE clause would hit the check constraint instead of overselling.
        await Assert.ThrowsAnyAsync<Exception>(() => db.Pools.Where(s => s.Id == pool.Id)
            .ExecuteUpdateAsync(s => s.SetProperty(x => x.HoursLeft, x => x.HoursLeft - 3)));
    }
}

using System.Collections.Concurrent;
using Compute.Api.Messaging;
using Compute.Api.Modules.Pools;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace Compute.Api.Tests;

public class PoolsConcurrencyTests
{
    [Fact]
    public async Task LastHour_TwoConcurrentReservations_OneHeldOneShortNoHoldForTheLoser()
    {
        var db = await TestDatabase.OpenAsync();
        var pool = await TestDatabase.AddPoolAsync(db, hourCapacity: 1);

        var outcomes = new ConcurrentBag<(Guid Reservation, ReserveOutcome Outcome)>();
        var gate = new TaskCompletionSource();
        var tasks = Enumerable.Range(0, 2).Select(n => Task.Run(async () =>
        {
            await gate.Task;
            var reservationId = Guid.CreateVersion7();
            var handler = new PoolsHandler(TestDatabase.Create());
            var outcome = await handler.ReserveAsync(
                Guid.CreateVersion7(), new ReserveHours(reservationId, pool.Id, 1, $"TEAM-{n}"));
            outcomes.Add((reservationId, outcome));
        })).ToArray();
        gate.SetResult();
        await Task.WhenAll(tasks);

        var check = TestDatabase.Create();
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
        var db = await TestDatabase.OpenAsync();
        var pool = await TestDatabase.AddPoolAsync(db, hourCapacity: 4);
        var reservationId = Guid.CreateVersion7();
        var handler = new PoolsHandler(TestDatabase.Create());
        await handler.ReserveAsync(Guid.CreateVersion7(), new ReserveHours(reservationId, pool.Id, 3, "TEAM-1"));

        // Two different commands (poison compensation and a cancellation) both ask for the same hours back.
        var first = await new PoolsHandler(TestDatabase.Create())
            .ReleaseAsync(Guid.CreateVersion7(), new ReleaseHours(reservationId, "poison"));
        var second = await new PoolsHandler(TestDatabase.Create())
            .ReleaseAsync(Guid.CreateVersion7(), new ReleaseHours(reservationId, "cancellation"));

        var remaining = await TestDatabase.Create().Pools.Where(s => s.Id == pool.Id).Select(s => s.HoursLeft).FirstAsync();
        Assert.True(first);
        Assert.False(second);
        Assert.Equal(4, remaining);
    }

    [Fact]
    public async Task SameMessageDeliveredTwice_IsClaimedOnce()
    {
        var db = await TestDatabase.OpenAsync();
        var pool = await TestDatabase.AddPoolAsync(db, hourCapacity: 5);
        var messageId = Guid.CreateVersion7();
        var command = new ReserveHours(Guid.CreateVersion7(), pool.Id, 2, "TEAM-1");

        var first = await new PoolsHandler(TestDatabase.Create()).ReserveAsync(messageId, command);
        var second = await new PoolsHandler(TestDatabase.Create()).ReserveAsync(messageId, command);

        var remaining = await TestDatabase.Create().Pools.Where(s => s.Id == pool.Id).Select(s => s.HoursLeft).FirstAsync();
        Assert.Equal(ReserveOutcome.Held, first);
        Assert.Equal(ReserveOutcome.Duplicate, second);
        Assert.Equal(3, remaining);
    }
}

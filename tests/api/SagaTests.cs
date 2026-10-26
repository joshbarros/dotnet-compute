using Compute.Api.Messaging;
using Compute.Api.Modules.Reservations;
using Compute.Api.Modules.Pools;
using Compute.Api.Modules.Leases;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Xunit;

namespace Compute.Api.Tests;

public class SagaTests
{
    private static ReservationsHandler Reservations() =>
        new(TestDatabase.Create(), Options.Create(new ReservationOptions()));

    [Fact]
    public async Task PoisonReference_IsCommittedRejected_ThenCompensatingCommandIsQueued()
    {
        var db = await TestDatabase.OpenAsync();
        var pool = await TestDatabase.AddPoolAsync(db, 5);
        var reservation = await TestDatabase.AddReservationAsync(db, pool, "TEAM-POISON-1", 2, ReservationStatus.Held);
        var held = new HoursHeld(reservation.Id, pool.Id, 2, reservation.Reference);

        var handler = new LeasesHandler(TestDatabase.Create());
        var ex = await Assert.ThrowsAsync<PoisonMessageException>(() =>
            handler.HandleAsync(new Delivery(Guid.CreateVersion7(), Topology.HeldKey, Json.Serialize(held)), default));
        Assert.Contains("poison", ex.Message, StringComparison.OrdinalIgnoreCase);

        var check = TestDatabase.Create();
        var doc = await check.Leases.SingleAsync(d => d.ReservationId == reservation.Id);
        Assert.Equal(LeaseStatus.Rejected, doc.Status); // committed before the dead-letter
        Assert.Equal(1, await check.Outbox.CountAsync(o => o.RoutingKey == Topology.ReleaseKey));
        Assert.Equal(1, await check.Outbox.CountAsync(o => o.RoutingKey == Topology.RejectedKey));
        Assert.Equal(0, await check.Outbox.CountAsync(o => o.RoutingKey == Topology.IssuedKey));
    }

    [Fact]
    public async Task Cancellation_ThenFinalMark_LeavesReservationCancelledAndLeaseIssued()
    {
        var db = await TestDatabase.OpenAsync();
        var pool = await TestDatabase.AddPoolAsync(db, 5);
        var reservation = await TestDatabase.AddReservationAsync(db, pool, "TEAM-1", 2, ReservationStatus.Held);

        // The lease is issued; the final mark has not landed yet.
        var number = "LS-TEST";
        await new LeasesHandler(TestDatabase.Create()).IssueAsync(
            Guid.CreateVersion7(), new HoursHeld(reservation.Id, pool.Id, 2, reservation.Reference));

        // The cancellation gets there first.
        await new ReservationService(TestDatabase.Create()).CancelAsync(reservation.Id, default);

        var mark = await Reservations().MarkLeasedAsync(Guid.CreateVersion7(), new LeaseIssued(reservation.Id, number));

        var check = TestDatabase.Create();
        Assert.Equal(MarkOutcome.Skipped, mark);
        Assert.Equal(ReservationStatus.Cancelled, (await check.Reservations.SingleAsync()).Status);
        Assert.Equal(LeaseStatus.Issued, (await check.Leases.SingleAsync()).Status);
    }

    [Fact]
    public async Task FinalMark_RunsOnlyFromHeld()
    {
        var db = await TestDatabase.OpenAsync();
        var pool = await TestDatabase.AddPoolAsync(db, 5);
        var held = await TestDatabase.AddReservationAsync(db, pool, "TEAM-A", 1, ReservationStatus.Held);
        var rejected = await TestDatabase.AddReservationAsync(db, pool, "TEAM-B", 1, ReservationStatus.Rejected);
        var requested = await TestDatabase.AddReservationAsync(db, pool, "TEAM-C", 1, ReservationStatus.Requested);

        Assert.Equal(MarkOutcome.Marked, await Reservations().MarkLeasedAsync(Guid.CreateVersion7(), new LeaseIssued(held.Id, "LS-1")));
        Assert.Equal(MarkOutcome.Skipped, await Reservations().MarkLeasedAsync(Guid.CreateVersion7(), new LeaseIssued(rejected.Id, "LS-2")));
        // Not Held yet: the message is retried later instead of being dropped.
        await Assert.ThrowsAsync<OutOfOrderException>(() =>
            Reservations().MarkLeasedAsync(Guid.CreateVersion7(), new LeaseIssued(requested.Id, "LS-3")));

        var statuses = (await TestDatabase.Create().Reservations.ToListAsync()).ToDictionary(b => b.Reference, b => b.Status);
        Assert.Equal(ReservationStatus.Leased, statuses["TEAM-A"]);
        Assert.Equal(ReservationStatus.Rejected, statuses["TEAM-B"]);
        Assert.Equal(ReservationStatus.Requested, statuses["TEAM-C"]);
    }

    [Fact]
    public async Task Cancellation_OfARejectedReservation_Conflicts()
    {
        var db = await TestDatabase.OpenAsync();
        var pool = await TestDatabase.AddPoolAsync(db, 5);
        var reservation = await TestDatabase.AddReservationAsync(db, pool, "TEAM-1", 1, ReservationStatus.Rejected);

        var ex = await Assert.ThrowsAsync<DomainException>(() => new ReservationService(TestDatabase.Create()).CancelAsync(reservation.Id, default));
        Assert.Equal(409, ex.Status);
    }
}

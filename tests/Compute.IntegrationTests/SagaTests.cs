using Compute.Reservations;
using Compute.Leases;
using Compute.Shared;
using Compute.Shared.Messaging;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Xunit;

namespace Compute.IntegrationTests;

public class SagaTests
{
    private static ReservationsHandler Handler(ReservationsDbContext db) => new(db, Options.Create(new ReservationOptions()));

    [Fact]
    public async Task PoisonReference_IsCommittedRejected_ThenCompensatingCommandIsQueued()
    {
        await TestDatabase.OpenAsync();
        var pool = await TestDatabase.AddPoolAsync(5);
        var reservation = await TestDatabase.AddReservationAsync(pool, "TEAM-POISON-1", 2, ReservationStatus.Held);
        var held = new HoursHeld(reservation.Id, reservation.OwnerId, pool.Id, 2, reservation.Reference);

        await using var docs = TestDatabase.Leases();
        var ex = await Assert.ThrowsAsync<PoisonMessageException>(() =>
            new LeasesHandler(docs).HandleAsync(new Delivery(Guid.CreateVersion7(), Topology.HeldKey, Json.Serialize(held)), default));
        Assert.Contains("poison", ex.Message, StringComparison.OrdinalIgnoreCase);

        await using var check = TestDatabase.Leases();
        var doc = await check.Leases.SingleAsync(d => d.ReservationId == reservation.Id);
        Assert.Equal(LeaseStatus.Rejected, doc.Status); // committed before the dead-letter
        Assert.Equal(reservation.OwnerId, doc.OwnerId);
        Assert.Equal(1, await check.Outbox.CountAsync(o => o.RoutingKey == Topology.ReleaseKey));
        Assert.Equal(1, await check.Outbox.CountAsync(o => o.RoutingKey == Topology.RejectedKey));
        Assert.Equal(0, await check.Outbox.CountAsync(o => o.RoutingKey == Topology.IssuedKey));
    }

    [Fact]
    public async Task Cancellation_ThenFinalMark_LeavesReservationCancelledAndLeaseIssued()
    {
        await TestDatabase.OpenAsync();
        var pool = await TestDatabase.AddPoolAsync(5);
        var owner = Guid.CreateVersion7();
        var reservation = await TestDatabase.AddReservationAsync(pool, "TEAM-1", 2, ReservationStatus.Held, owner);

        // The lease is issued; the final mark has not landed yet.
        await using (var docs = TestDatabase.Leases())
            await new LeasesHandler(docs).IssueAsync(Guid.CreateVersion7(), new HoursHeld(reservation.Id, owner, pool.Id, 2, reservation.Reference));

        // The owner reverses first.
        await using (var reservations = TestDatabase.Reservations())
            await new ReservationService(reservations, new FixedCatalog(pool), new TestUser(owner)).CancelAsync(reservation.Id, default);

        await using var late = TestDatabase.Reservations();
        var mark = await Handler(late).MarkLeasedAsync(Guid.CreateVersion7(), new LeaseIssued(reservation.Id, "LS-TEST"));

        await using var check = TestDatabase.Reservations();
        await using var checkDocs = TestDatabase.Leases();
        Assert.Equal(MarkOutcome.Skipped, mark);
        Assert.Equal(ReservationStatus.Cancelled, (await check.Reservations.SingleAsync()).Status);
        Assert.Equal(LeaseStatus.Issued, (await checkDocs.Leases.SingleAsync()).Status);
    }

    [Fact]
    public async Task FinalMark_RunsOnlyFromHeld()
    {
        await TestDatabase.OpenAsync();
        var pool = await TestDatabase.AddPoolAsync(5);
        var held = await TestDatabase.AddReservationAsync(pool, "TEAM-A", 1, ReservationStatus.Held);
        var rejected = await TestDatabase.AddReservationAsync(pool, "TEAM-B", 1, ReservationStatus.Rejected);
        var requested = await TestDatabase.AddReservationAsync(pool, "TEAM-C", 1, ReservationStatus.Requested);

        await using (var a = TestDatabase.Reservations())
            Assert.Equal(MarkOutcome.Marked, await Handler(a).MarkLeasedAsync(Guid.CreateVersion7(), new LeaseIssued(held.Id, "LS-1")));
        await using (var b = TestDatabase.Reservations())
            Assert.Equal(MarkOutcome.Skipped, await Handler(b).MarkLeasedAsync(Guid.CreateVersion7(), new LeaseIssued(rejected.Id, "LS-2")));
        // Not Held yet: the message is retried later instead of being dropped.
        await using (var c = TestDatabase.Reservations())
            await Assert.ThrowsAsync<OutOfOrderException>(() => Handler(c).MarkLeasedAsync(Guid.CreateVersion7(), new LeaseIssued(requested.Id, "LS-3")));

        await using var check = TestDatabase.Reservations();
        var statuses = (await check.Reservations.ToListAsync()).ToDictionary(b => b.Reference, b => b.Status);
        Assert.Equal(ReservationStatus.Leased, statuses["TEAM-A"]);
        Assert.Equal(ReservationStatus.Rejected, statuses["TEAM-B"]);
        Assert.Equal(ReservationStatus.Requested, statuses["TEAM-C"]);
    }

    [Fact]
    public async Task Cancellation_OfARejectedReservation_Conflicts()
    {
        await TestDatabase.OpenAsync();
        var pool = await TestDatabase.AddPoolAsync(5);
        var owner = Guid.CreateVersion7();
        var reservation = await TestDatabase.AddReservationAsync(pool, "TEAM-1", 1, ReservationStatus.Rejected, owner);

        await using var db = TestDatabase.Reservations();
        var ex = await Assert.ThrowsAsync<DomainException>(() =>
            new ReservationService(db, new FixedCatalog(pool), new TestUser(owner)).CancelAsync(reservation.Id, default));
        Assert.Equal(409, ex.Status);
    }

    [Fact]
    public async Task Cancellation_ByAnotherCustomer_IsNotFound_AndChangesNothing()
    {
        await TestDatabase.OpenAsync();
        var pool = await TestDatabase.AddPoolAsync(5);
        var reservation = await TestDatabase.AddReservationAsync(pool, "TEAM-1", 1, ReservationStatus.Held);

        await using var db = TestDatabase.Reservations();
        var ex = await Assert.ThrowsAsync<DomainException>(() =>
            new ReservationService(db, new FixedCatalog(pool), new TestUser(Guid.NewGuid())).CancelAsync(reservation.Id, default));

        Assert.Equal(404, ex.Status);
        await using var check = TestDatabase.Reservations();
        Assert.Equal(ReservationStatus.Held, (await check.Reservations.SingleAsync()).Status);
        Assert.Equal(0, await check.Outbox.CountAsync());
    }

    [Fact]
    public async Task Staff_CanCancelAnyonesReservation()
    {
        await TestDatabase.OpenAsync();
        var pool = await TestDatabase.AddPoolAsync(5);
        var reservation = await TestDatabase.AddReservationAsync(pool, "TEAM-1", 1, ReservationStatus.Held);

        await using var db = TestDatabase.Reservations();
        var cancelled = await new ReservationService(db, new FixedCatalog(pool), new TestUser(Guid.NewGuid(), staff: true)).CancelAsync(reservation.Id, default);
        Assert.Equal(ReservationStatus.Cancelled, cancelled.Status);
    }

    [Fact]
    public async Task Create_RecordsTheCallerAsTheOwner_AndQueuesTheReservation()
    {
        await TestDatabase.OpenAsync();
        var pool = await TestDatabase.AddPoolAsync(5);
        var owner = Guid.CreateVersion7();

        await using var db = TestDatabase.Reservations();
        var reservation = await new ReservationService(db, new FixedCatalog(pool), new TestUser(owner))
            .CreateAsync(new CreateReservationRequest(pool.Code, "TEAM-1", 2), default);

        await using var check = TestDatabase.Reservations();
        Assert.Equal(owner, (await check.Reservations.SingleAsync()).OwnerId);
        var message = await check.Outbox.SingleAsync();
        Assert.Equal(Topology.ReserveKey, message.RoutingKey);
        Assert.Equal(owner, Json.Parse<ReserveHours>(message.Payload).OwnerId);
        Assert.Equal(ReservationStatus.Requested, reservation.Status);
    }

    [Fact]
    public async Task Create_WithoutASignedInUser_IsRefused()
    {
        await TestDatabase.OpenAsync();
        var pool = await TestDatabase.AddPoolAsync(5);
        await using var db = TestDatabase.Reservations();
        var ex = await Assert.ThrowsAsync<DomainException>(() =>
            new ReservationService(db, new FixedCatalog(pool), new TestUser(null)).CreateAsync(new CreateReservationRequest(pool.Code, "TEAM-1", 1), default));
        Assert.Equal(401, ex.Status);
    }

    [Fact]
    public async Task Create_ForAnUnknownPool_IsNotFound()
    {
        await TestDatabase.OpenAsync();
        await using var db = TestDatabase.Reservations();
        var ex = await Assert.ThrowsAsync<DomainException>(() =>
            new ReservationService(db, new FixedCatalog(), new TestUser(Guid.NewGuid())).CreateAsync(new CreateReservationRequest("NOPE", "TEAM-1", 1), default));
        Assert.Equal(404, ex.Status);
    }
}

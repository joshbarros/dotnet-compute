using Compute.Reservations;
using Compute.Pools;
using Xunit;

namespace Compute.UnitTests;

public class DtoTests
{
    [Fact]
    public void ReservationDto_MapsEveryFieldAndTheStatusName()
    {
        var now = DateTimeOffset.UtcNow;
        var reservation = new Reservation
        {
            Id = Guid.NewGuid(),
            OwnerId = Guid.NewGuid(),
            PoolId = Guid.NewGuid(),
            PoolCode = "S-1",
            Reference = "TEAM-1",
            Hours = 3,
            FeeCents = 277_500,
            Status = ReservationStatus.Leased,
            LeaseNumber = "LS-1",
            CreatedAt = now,
            UpdatedAt = now,
        };

        var dto = ReservationDto.From(reservation);

        Assert.Equal(reservation.Id, dto.Id);
        Assert.Equal(reservation.OwnerId, dto.OwnerId);
        Assert.Equal("Leased", dto.Status);
        Assert.Equal(277_500, dto.FeeCents);
        Assert.Equal("LS-1", dto.LeaseNumber);
    }

    [Fact]
    public void PoolDto_MapsTheScarceNumber()
    {
        var pool = new Pool { Id = Guid.NewGuid(), Code = "S-1", Name = "v", GpuModel = "o", Region = "d", HourCapacity = 10, HoursLeft = 4 };
        var dto = PoolDto.From(pool);
        Assert.Equal(10, dto.HourCapacity);
        Assert.Equal(4, dto.HoursLeft);
    }
}

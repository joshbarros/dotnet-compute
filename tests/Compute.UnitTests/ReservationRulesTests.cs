using Compute.Reservations;
using Xunit;

namespace Compute.UnitTests;

public class ReservationRulesTests
{
    [Theory]
    [InlineData(null, 1)]
    [InlineData("", 1)]
    [InlineData("   ", 1)]
    [InlineData("TEAM-1", 0)]
    [InlineData("TEAM-1", -3)]
    public void EmptyInput_IsRejected(string? reference, long hours) =>
        Assert.NotNull(ReservationRules.Validate(reference, hours));

    [Fact]
    public void OverLimit_IsRejected()
    {
        Assert.Null(ReservationRules.Validate("TEAM-1", ReservationRules.MaxHoursPerReservation));
        Assert.NotNull(ReservationRules.Validate("TEAM-1", ReservationRules.MaxHoursPerReservation + 1));
        Assert.NotNull(ReservationRules.Validate(new string('x', ReservationRules.MaxReferenceLength + 1), 1));
    }

    [Theory]
    [InlineData(int.MaxValue)]
    [InlineData((long)int.MaxValue + 1)]
    [InlineData(4_294_967_297L)] // (int) of this wraps to 1; it must not pass as one hour
    [InlineData(long.MaxValue)]
    public void IntegerOverflow_IsRejectedNotWrapped(long hours)
    {
        Assert.NotNull(ReservationRules.Validate("TEAM-1", hours));
        Assert.Equal(1, unchecked((int)4_294_967_297L)); // documents what the naive cast would have done
    }

    [Fact]
    public void HappyFee_IsTheRateWithAMinimumCharge()
    {
        Assert.Null(ReservationRules.Validate("TEAM-1", 10));
        Assert.Equal(10 * 320L, ReservationRules.FeeCents(10)); // $32.00 above the minimum
        Assert.Equal(ReservationRules.MinimumChargeCents, ReservationRules.FeeCents(1)); // $15.00 minimum
        Assert.Equal(ReservationRules.MinimumChargeCents, ReservationRules.FeeCents(4)); // 1,280 is under 1,500
        Assert.Equal(24 * 320L, ReservationRules.FeeCents(ReservationRules.MaxHoursPerReservation));
    }
}

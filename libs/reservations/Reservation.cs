namespace Compute.Reservations;

public enum ReservationStatus { Requested, Held, Leased, Cancelled, Rejected }

public class Reservation
{
    public Guid Id { get; set; }
    public Guid OwnerId { get; set; }
    public Guid PoolId { get; set; }
    public string PoolCode { get; set; } = "";
    public string Reference { get; set; } = "";
    public int Hours { get; set; }
    public long FeeCents { get; set; }
    public ReservationStatus Status { get; set; }
    public string? Reason { get; set; }
    public string? LeaseNumber { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
    public DateTimeOffset? HoursReleasedAt { get; set; }
}

public class ReservationOptions
{
    /// <summary>Latency knob for the final mark. Compose sets it so the cancellation race window is wide enough to hit on purpose.</summary>
    public int FinalMarkDelayMs { get; set; }
}

public record CreateReservationRequest(string? PoolCode, string? Reference, long Hours);

public record ReservationDto(
    Guid Id, Guid OwnerId, string PoolCode, string Reference, int Hours, long FeeCents, string Status,
    string? Reason, string? LeaseNumber, DateTimeOffset CreatedAt, DateTimeOffset UpdatedAt,
    DateTimeOffset? HoursReleasedAt)
{
    public static ReservationDto From(Reservation b) => new(
        b.Id, b.OwnerId, b.PoolCode, b.Reference, b.Hours, b.FeeCents, b.Status.ToString(),
        b.Reason, b.LeaseNumber, b.CreatedAt, b.UpdatedAt, b.HoursReleasedAt);
}

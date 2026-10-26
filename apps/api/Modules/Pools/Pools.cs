namespace Compute.Api.Modules.Pools;

public class Pool
{
    public Guid Id { get; set; }
    public string Code { get; set; } = "";
    public string Name { get; set; } = "";
    public string GpuModel { get; set; } = "";
    public string Region { get; set; } = "";
    public DateTimeOffset Day { get; set; }
    public int HourCapacity { get; set; }
    public int HoursLeft { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
}

public enum HoldStatus { Held, Released }

public class HourHold
{
    public Guid ReservationId { get; set; }
    public Guid PoolId { get; set; }
    public int Hours { get; set; }
    public HoldStatus Status { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset? ReleasedAt { get; set; }
    public string? ReleaseReason { get; set; }
}

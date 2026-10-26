using System.Text.Json;

namespace Compute.Shared.Messaging;

public record ReserveHours(Guid ReservationId, Guid OwnerId, Guid PoolId, int Hours, string Reference);
public record HoursHeld(Guid ReservationId, Guid OwnerId, Guid PoolId, int Hours, string Reference);
public record HoursShort(Guid ReservationId, string Reference, int RequestedHours, int HoursLeft);
public record ReleaseHours(Guid ReservationId, string Reason);
public record HoursReleased(Guid ReservationId, int Hours, string Reason);
public record LeaseIssued(Guid ReservationId, string Number);
public record LeaseRejected(Guid ReservationId, string Reason);

public record Delivery(Guid MessageId, string RoutingKey, string Body);

public interface IMessageHandler
{
    Task HandleAsync(Delivery delivery, CancellationToken ct);
}

/// <summary>Committed as rejected; the message must be dead-lettered, not retried.</summary>
public class PoisonMessageException(string message) : Exception(message);

/// <summary>The message arrived before the state it depends on; retry later.</summary>
public class OutOfOrderException(string message) : Exception(message);

public static class Json
{
    public static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web);

    public static string Serialize<T>(T value) => JsonSerializer.Serialize(value, Options);

    public static T Parse<T>(string body) =>
        JsonSerializer.Deserialize<T>(body, Options) ?? throw new InvalidOperationException("Empty message body.");
}

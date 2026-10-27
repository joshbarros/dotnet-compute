using Compute.Shared.Messaging;
using Xunit;

namespace Compute.UnitTests;

public class ConsumerRetryPolicyTests
{
    [Fact]
    public void PoisonMessage_IsDeadLetteredAtOnce() =>
        Assert.Equal(FailureAction.DeadLetter, ConsumerRetryPolicy.Decide(0, new PoisonMessageException("poison")));

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    public void TransientFailure_RetriesWhileAttemptsRemain(int priorAttempts) =>
        Assert.Equal(FailureAction.Retry, ConsumerRetryPolicy.Decide(priorAttempts, new InvalidOperationException("db down")));

    [Fact]
    public void TransientFailure_IsDeadLetteredOnTheLastAttempt() =>
        Assert.Equal(FailureAction.DeadLetter, ConsumerRetryPolicy.Decide(Topology.MaxAttempts - 1, new InvalidOperationException("db down")));

    [Fact]
    public void OutOfOrderMessage_IsRetriedLikeAnyTransientFailure() =>
        Assert.Equal(FailureAction.Retry, ConsumerRetryPolicy.Decide(0, new OutOfOrderException("not yet")));
}

public class TopologyTests
{
    private static readonly string[] Keys =
    [
        Topology.ReserveKey, Topology.ReleaseKey, Topology.HeldKey, Topology.ShortKey,
        Topology.ReleasedKey, Topology.IssuedKey, Topology.RejectedKey,
    ];

    [Fact]
    public void RoutingKeys_AreDistinct() => Assert.Equal(Keys.Length, Keys.Distinct().Count());

    [Fact]
    public void Queues_AreDistinct()
    {
        string[] queues = [Topology.PoolsQueue, Topology.LeasesQueue, Topology.ReservationsQueue];
        Assert.Equal(queues.Length, queues.Distinct().Count());
    }

    [Fact]
    public void Exchanges_AreDistinctAndNamedForTheSystem()
    {
        string[] exchanges = [Topology.Exchange, Topology.DeadLetterExchange, Topology.RequeueExchange];
        Assert.Equal(exchanges.Length, exchanges.Distinct().Count());
        Assert.All(exchanges, e => Assert.StartsWith("compute.", e));
        Assert.Equal("compute.events", Topology.KafkaTopic);
    }

    [Fact]
    public void Retries_AreBoundedAndDelayed()
    {
        Assert.InRange(Topology.MaxAttempts, 2, 10);
        Assert.InRange(Topology.RetryDelayMs, 500, 60_000);
    }
}

public class MessageSerializationTests
{
    [Fact]
    public void PoolsHeld_RoundTripsWithItsOwner()
    {
        var held = new HoursHeld(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), 3, "TEAM-1");
        Assert.Equal(held, Json.Parse<HoursHeld>(Json.Serialize(held)));
    }

    [Fact]
    public void Serialization_UsesCamelCase() =>
        Assert.Contains("\"reservationId\"", Json.Serialize(new ReleaseHours(Guid.NewGuid(), "poison")));

    [Fact]
    public void Parse_IgnoresFieldsAddedByANewerPublisher()
    {
        var id = Guid.NewGuid();
        var parsed = Json.Parse<ReleaseHours>($$"""{"reservationId":"{{id}}","reason":"poison","somethingNew":42}""");
        Assert.Equal(new ReleaseHours(id, "poison"), parsed);
    }

    [Fact]
    public void Parse_RejectsAnEmptyBody() => Assert.ThrowsAny<Exception>(() => Json.Parse<ReleaseHours>("null"));
}

public class DeadLetterTests
{
    [Fact]
    public void TheRoutingKey_ComesFromTheTypeProperty()
    {
        var props = new RabbitMQ.Client.BasicProperties { Type = "hours.reserve" };
        Assert.Equal("hours.reserve", DeadLetterService.RoutingKeyOf(props));
    }

    [Fact]
    public void WithoutAType_ThereIsNothingToReplayTo() =>
        Assert.Null(DeadLetterService.RoutingKeyOf(new RabbitMQ.Client.BasicProperties()));
}

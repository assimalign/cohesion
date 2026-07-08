using Shouldly;

namespace Assimalign.Cohesion.Health.Tests;

public class HealthCheckPredicatesTests
{
    [Fact(DisplayName = "Cohesion Test [Health] - Predicates: Ready matches only ready-tagged registrations")]
    public void Ready_ShouldMatchReadyTaggedRegistrationsOnly()
    {
        HealthCheckRegistration ready = Registration("db", HealthTags.Ready);
        HealthCheckRegistration live = Registration("self", HealthTags.Live);

        HealthCheckPredicates.Ready(ready).ShouldBeTrue();
        HealthCheckPredicates.Ready(live).ShouldBeFalse();
    }

    [Fact(DisplayName = "Cohesion Test [Health] - Predicates: Live matches only live-tagged registrations")]
    public void Live_ShouldMatchLiveTaggedRegistrationsOnly()
    {
        HealthCheckRegistration live = Registration("self", HealthTags.Live);
        HealthCheckRegistration ready = Registration("db", HealthTags.Ready);

        HealthCheckPredicates.Live(live).ShouldBeTrue();
        HealthCheckPredicates.Live(ready).ShouldBeFalse();
    }

    [Fact(DisplayName = "Cohesion Test [Health] - Predicates: WithAnyTag matches on any supplied tag")]
    public void WithAnyTag_ShouldMatchWhenAnyTagPresent()
    {
        var predicate = HealthCheckPredicates.WithAnyTag("sql", "cache");

        predicate(Registration("db", "sql")).ShouldBeTrue();
        predicate(Registration("redis", "cache")).ShouldBeTrue();
        predicate(Registration("queue", "amqp")).ShouldBeFalse();
    }

    [Fact(DisplayName = "Cohesion Test [Health] - Predicates: WithAnyTag with no tags matches everything")]
    public void WithAnyTag_WhenNoTagsSupplied_ShouldMatchEverything()
    {
        var predicate = HealthCheckPredicates.WithAnyTag();

        predicate(Registration("anything")).ShouldBeTrue();
    }

    private static HealthCheckRegistration Registration(string name, params string[] tags)
        => new(name, new StubCheck(HealthStatus.Healthy), tags: tags);
}

using Shouldly;
using Xunit;

using Assimalign.Cohesion.Hosting.Resources;

namespace Assimalign.Cohesion.Hosting.Resources.Tests;

public class HealthStatusTests
{
    private const string DisplayPrefix = "Cohesion Test [Hosting] - HealthStatus: ";

    [Fact(DisplayName = DisplayPrefix + "Values remain ordered from least to most healthy")]
    public void Values_WhenCompared_ShouldPreserveAggregationOrder()
    {
        // Arrange & Act & Assert
        ((int)HealthStatus.Unhealthy).ShouldBe(0);
        ((int)HealthStatus.Degraded).ShouldBe(1);
        ((int)HealthStatus.Healthy).ShouldBe(2);
    }
}

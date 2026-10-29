using Shouldly;
using Xunit;

namespace Assimalign.Cohesion.ApplicationModel.Hosting.Tests;

public class HealthContributionTests
{
    private const string DisplayPrefix = "Cohesion Test [Hosting] - HealthContribution: ";

    [Fact(DisplayName = DisplayPrefix + "A contribution nobody set fails closed as unhealthy")]
    public void Default_WhenObserved_ShouldBeUnhealthy()
    {
        // Arrange & Act
        HealthContribution contribution = default;

        // Assert
        contribution.Status.ShouldBe(ResourceHealthStatus.Unhealthy);
    }

    [Fact(DisplayName = DisplayPrefix + "Factories report their status")]
    public void Factories_WithDescription_ShouldReportStatus()
    {
        // Arrange & Act
        HealthContribution healthy = HealthContribution.Healthy("ready");
        HealthContribution degraded = HealthContribution.Degraded("slow");
        HealthContribution unhealthy = HealthContribution.Unhealthy("offline");

        // Assert
        healthy.ShouldBe(new HealthContribution(ResourceHealthStatus.Healthy, "ready"));
        degraded.ShouldBe(new HealthContribution(ResourceHealthStatus.Degraded, "slow"));
        unhealthy.ShouldBe(new HealthContribution(ResourceHealthStatus.Unhealthy, "offline"));
    }
}

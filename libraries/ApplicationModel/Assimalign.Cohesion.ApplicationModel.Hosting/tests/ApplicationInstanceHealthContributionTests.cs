using Shouldly;
using Xunit;

namespace Assimalign.Cohesion.ApplicationModel.Hosting.Tests;

public class ApplicationInstanceHealthContributionTests
{
    private const string DisplayPrefix = "Cohesion Test [Hosting] - ApplicationInstanceHealthContribution: ";

    [Fact(DisplayName = DisplayPrefix + "A contribution nobody set fails closed as unhealthy")]
    public void Default_WhenObserved_ShouldBeUnhealthy()
    {
        // Arrange & Act
        ApplicationInstanceHealthContribution contribution = default;

        // Assert
        contribution.Status.ShouldBe(ApplicationInstanceHealthStatus.Unhealthy);
    }

    [Fact(DisplayName = DisplayPrefix + "Factories report their status")]
    public void Factories_WithDescription_ShouldReportStatus()
    {
        // Arrange & Act
        ApplicationInstanceHealthContribution healthy = ApplicationInstanceHealthContribution.Healthy("ready");
        ApplicationInstanceHealthContribution degraded = ApplicationInstanceHealthContribution.Degraded("slow");
        ApplicationInstanceHealthContribution unhealthy = ApplicationInstanceHealthContribution.Unhealthy("offline");

        // Assert
        healthy.ShouldBe(new ApplicationInstanceHealthContribution(ApplicationInstanceHealthStatus.Healthy, "ready"));
        degraded.ShouldBe(new ApplicationInstanceHealthContribution(ApplicationInstanceHealthStatus.Degraded, "slow"));
        unhealthy.ShouldBe(new ApplicationInstanceHealthContribution(ApplicationInstanceHealthStatus.Unhealthy, "offline"));
    }
}

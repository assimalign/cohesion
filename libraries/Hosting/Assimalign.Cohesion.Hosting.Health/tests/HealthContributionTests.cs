using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

using Shouldly;
using Xunit;

namespace Assimalign.Cohesion.Hosting.Health.Tests;

public class HealthContributionTests
{
    private const string DisplayPrefix = "Cohesion Test [Hosting] - HealthContribution: ";

    [Fact(DisplayName = DisplayPrefix + "Factories preserve status, description, and data")]
    public void Factories_WithDiagnosticValues_ShouldCreateExpectedContributions()
    {
        // Arrange
        IReadOnlyDictionary<string, object> data = new Dictionary<string, object>
        {
            ["latencyMs"] = 42
        };

        // Act
        HealthContribution healthy = HealthContribution.Healthy("ready", data);
        HealthContribution degraded = HealthContribution.Degraded("slow", data);
        HealthContribution unhealthy = HealthContribution.Unhealthy("offline", data);

        // Assert
        healthy.ShouldBe(new HealthContribution(HealthStatus.Healthy, "ready", data));
        degraded.ShouldBe(new HealthContribution(HealthStatus.Degraded, "slow", data));
        unhealthy.ShouldBe(new HealthContribution(HealthStatus.Unhealthy, "offline", data));
    }

    [Fact(DisplayName = DisplayPrefix + "Default value fails closed as unhealthy")]
    public void Default_WhenObserved_ShouldBeUnhealthy()
    {
        // Arrange & Act
        HealthContribution contribution = default;

        // Assert
        contribution.Status.ShouldBe(HealthStatus.Unhealthy);
        contribution.Description.ShouldBeNull();
        contribution.Data.ShouldBeNull();
    }

    [Fact(DisplayName = DisplayPrefix + "Contributor exposes a named asynchronous health snapshot")]
    public async Task CheckAsync_WithContributor_ShouldReturnHealthSnapshot()
    {
        // Arrange
        IHealthContributor contributor = new TestHealthContributor(
            "database",
            HealthContribution.Healthy("accepting connections"));

        // Act
        HealthContribution contribution = await contributor.CheckAsync(CancellationToken.None);

        // Assert
        contributor.Name.ShouldBe("database");
        contribution.Status.ShouldBe(HealthStatus.Healthy);
        contribution.Description.ShouldBe("accepting connections");
    }

    private sealed class TestHealthContributor : IHealthContributor
    {
        private readonly HealthContribution _contribution;

        public TestHealthContributor(string name, HealthContribution contribution)
        {
            Name = name;
            _contribution = contribution;
        }

        public string Name { get; }

        public ValueTask<HealthContribution> CheckAsync(CancellationToken cancellationToken = default)
        {
            return ValueTask.FromResult(_contribution);
        }
    }
}

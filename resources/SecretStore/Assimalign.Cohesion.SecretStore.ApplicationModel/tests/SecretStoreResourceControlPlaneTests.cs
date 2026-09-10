using System;
using System.Threading.Tasks;

using Shouldly;
using Xunit;

using Assimalign.Cohesion.Hosting.Health;
using Assimalign.Cohesion.Hosting.Resources;

namespace Assimalign.Cohesion.SecretStore.ApplicationModel.Tests;

public sealed class SecretStoreResourceControlPlaneTests
{
    [Fact(DisplayName = "Cohesion Test [SecretStore.ApplicationModel] - Default control plane: creates an isolated plane with the trust-grant command")]
    public void Create_WhenCalled_ShouldReturnIsolatedPlaneWithTrustGrantCommand()
    {
        // Act
        IResourceControlPlane first = SecretStoreResourceControlPlane.Create();
        IResourceControlPlane second = SecretStoreResourceControlPlane.Create();

        // Assert
        first.ShouldNotBeSameAs(second);
        first.AcceptedCommandKinds.ShouldBe(["cohesion.trust.add"]);
        first.ObservedEndpoints.ShouldBeEmpty();
    }

    [Fact(DisplayName = "Cohesion Test [SecretStore.ApplicationModel] - Default control plane: reports healthy readiness and liveness before contributors are added")]
    public async Task CheckReadinessAndLivenessAsync_WithNoContributors_ShouldReturnHealthyReports()
    {
        // Arrange
        IResourceControlPlane controlPlane = SecretStoreResourceControlPlane.Create();

        // Act
        ResourceHealthReport readiness = await controlPlane.CheckReadinessAsync();
        ResourceHealthReport liveness = await controlPlane.CheckLivenessAsync();

        // Assert
        readiness.Status.ShouldBe(HealthStatus.Healthy);
        readiness.Contributions.ShouldBeEmpty();
        liveness.Status.ShouldBe(HealthStatus.Healthy);
        liveness.Contributions.ShouldBeEmpty();
    }

    [Fact(DisplayName = "Cohesion Test [SecretStore.ApplicationModel] - Default control plane: exposes the observed API endpoint")]
    public void ObserveEndpoint_WithApiEndpoint_ShouldExposeEndpointSnapshot()
    {
        // Arrange
        IResourceControlPlane controlPlane = SecretStoreResourceControlPlane.Create();
        var endpoint = new Uri("https://127.0.0.1:8443");

        // Act
        controlPlane.ObserveEndpoint("api", endpoint);

        // Assert
        controlPlane.ObservedEndpoints["api"].ShouldBe(endpoint);
    }
}

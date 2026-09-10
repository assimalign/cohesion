using Assimalign.Cohesion.Hosting.Resources;

using Shouldly;
using Xunit;

namespace Assimalign.Cohesion.IdentityHub.ApplicationModel.Tests;

public sealed class IdentityHubResourceControlPlaneTests
{
    [Fact(DisplayName = "Cohesion Test [IdentityHub.ApplicationModel] - Default control plane: creates an isolated plane with no command kinds")]
    public void Create_ShouldReturnDefaultPlaneWithNoCommandKinds()
    {
        // Act
        IResourceControlPlane first = IdentityHubResourceControlPlane.Create();
        IResourceControlPlane second = IdentityHubResourceControlPlane.Create();

        // Assert
        first.ShouldNotBeSameAs(second);
        first.AcceptedCommandKinds.ShouldBeEmpty();
        first.ObservedEndpoints.ShouldBeEmpty();
    }
}

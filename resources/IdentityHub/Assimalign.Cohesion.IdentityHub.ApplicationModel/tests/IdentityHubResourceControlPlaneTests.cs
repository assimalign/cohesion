using Assimalign.Cohesion.ApplicationModel;
using Assimalign.Cohesion.Hosting.Resources;

using Shouldly;
using Xunit;

namespace Assimalign.Cohesion.IdentityHub.ApplicationModel.Tests;

public sealed class IdentityHubResourceControlPlaneTests
{
    [Fact(DisplayName = "Cohesion Test [IdentityHub.ApplicationModel] - Default control plane: creates an isolated plane with identity command kinds")]
    public void Create_ShouldReturnDefaultPlaneWithIdentityCommandKinds()
    {
        // Act
        IResourceControlPlane first = IdentityHubResourceControlPlane.Create();
        IResourceControlPlane second = IdentityHubResourceControlPlane.Create();

        // Assert
        first.ShouldNotBeSameAs(second);
        first.AcceptedCommandKinds.ShouldBe(["identityhub.add-audience", "identityhub.add-client"]);
        first.ObservedEndpoints.ShouldBeEmpty();
    }
}

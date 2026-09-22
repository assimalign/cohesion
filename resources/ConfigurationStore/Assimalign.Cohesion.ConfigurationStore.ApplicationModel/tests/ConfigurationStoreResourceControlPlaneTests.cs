using Assimalign.Cohesion.ApplicationModel;
using Assimalign.Cohesion.Hosting.Resources;

using Shouldly;
using Xunit;

namespace Assimalign.Cohesion.ConfigurationStore.ApplicationModel.Tests;

public sealed class ConfigurationStoreResourceControlPlaneTests
{
    [Fact(DisplayName = "Cohesion Test [ConfigurationStore.ApplicationModel] - Default control plane: creates an isolated plane with store command kinds")]
    public void Create_ShouldReturnDefaultPlaneWithStoreCommandKinds()
    {
        // Act
        IResourceControlPlane first = ConfigurationStoreResourceControlPlane.Create();
        IResourceControlPlane second = ConfigurationStoreResourceControlPlane.Create();

        // Assert
        first.ShouldNotBeSameAs(second);
        first.AcceptedCommandKinds.ShouldBe(
        [
            "configurationstore.add-namespace",
            "configurationstore.set-value",
            "configurationstore.remove-value",
        ]);
        first.ObservedEndpoints.ShouldBeEmpty();
    }
}

using Assimalign.Cohesion.Hosting;

using Shouldly;

using Xunit;

namespace Assimalign.Cohesion.Database.ApplicationModel.Tests;

public sealed class DatabaseResourceControlPlaneTests
{
    [Fact(DisplayName = "Cohesion Test [Database.ApplicationModel] - Default control plane: creates an isolated plane with no command kinds")]
    public void Create_ShouldReturnDefaultPlaneWithNoCommandKinds()
    {
        IResourceControlPlane first = DatabaseResourceControlPlane.Create();
        IResourceControlPlane second = DatabaseResourceControlPlane.Create();

        first.ShouldNotBeSameAs(second);
        first.AcceptedCommandKinds.ShouldBeEmpty();
        first.ObservedEndpoints.ShouldBeEmpty();
    }
}

using Assimalign.Cohesion.ApplicationModel;
using Assimalign.Cohesion.Hosting.Resources;

using Shouldly;

using Xunit;

namespace Assimalign.Cohesion.Web.ApplicationModel.Tests;

public sealed class WebResourceControlPlaneTests
{
    [Fact(DisplayName = "Cohesion Test [Web.ApplicationModel] - Default control plane: creates an isolated plane with no command kinds")]
    public void Create_ShouldReturnDefaultPlaneWithNoCommandKinds()
    {
        IResourceControlPlane first = WebResourceControlPlane.Create();
        IResourceControlPlane second = WebResourceControlPlane.Create();

        first.ShouldNotBeSameAs(second);
        first.AcceptedCommandKinds.ShouldBeEmpty();
        first.ObservedEndpoints.ShouldBeEmpty();
    }
}

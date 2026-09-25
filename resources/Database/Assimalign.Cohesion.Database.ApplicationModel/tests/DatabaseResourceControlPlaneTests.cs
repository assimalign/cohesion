using Assimalign.Cohesion.ApplicationModel;
using Assimalign.Cohesion.Hosting.Resources;

using Shouldly;

using Xunit;

namespace Assimalign.Cohesion.Database.ApplicationModel.Tests;

public sealed class DatabaseResourceControlPlaneTests
{
    [Fact(DisplayName = "Cohesion Test [Database.ApplicationModel] - Default control plane: creates an isolated plane with database command kinds")]
    public void Create_WithIndependentCalls_ShouldReturnDatabaseCommandKinds()
    {
        IResourceControlPlane first = DatabaseResourceControlPlane.Create();
        IResourceControlPlane second = DatabaseResourceControlPlane.Create();

        first.ShouldNotBeSameAs(second);
        first.AcceptedCommandKinds.ShouldBe(new[] { "database.add-database", "database.add-principal" }, ignoreOrder: true);
        first.ObservedEndpoints.ShouldBeEmpty();
    }
}

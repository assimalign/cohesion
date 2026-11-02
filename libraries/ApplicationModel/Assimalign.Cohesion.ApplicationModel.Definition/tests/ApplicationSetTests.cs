using System;
using System.Linq;

using Shouldly;
using Xunit;

namespace Assimalign.Cohesion.ApplicationModel.Definition.Tests;

public class ApplicationSetTests
{
    private const string DisplayPrefix = "Cohesion Test [ApplicationModel] - ApplicationSet: ";

    [Fact(DisplayName = DisplayPrefix + "A set refuses a second resource with one provider and name")]
    public void AddResource_WithDuplicateInSet_ShouldThrow()
    {
        // Arrange
        IApplicationModelBuilder set = ApplicationModel.CreateBuilder("root");
        set.AddResource("Web", "orders-api");

        // Act & Assert
        Should.Throw<InvalidOperationException>(() => set.AddResource("web", "orders-api"));
    }

    [Fact(DisplayName = DisplayPrefix + "A name may repeat under another provider")]
    public void AddResource_WithNameUnderAnotherProvider_ShouldSucceed()
    {
        // Arrange
        IApplicationModelBuilder set = ApplicationModel.CreateBuilder("root");

        // Act
        set.AddResource("Web", "orders");
        set.AddResource("Database.Sql", "orders");

        // Assert
        set.Resources.Count.ShouldBe(2);
    }

    [Fact(DisplayName = DisplayPrefix + "The manifest projects the set and its parent in declaration order")]
    public void GetManifest_WithResources_ShouldProjectInDeclarationOrder()
    {
        // Arrange
        IApplicationModelBuilder set = ApplicationModel.CreateBuilder("east");
        set.UseParent("root");
        IApplicationResourceDefinition database = set.AddResource("Database.Sql", "orders-db");
        IApplicationResourceDefinition cache = set.AddResource("Caching", "orders-cache");
        set.AddResource("Web", "orders-api")
            .DependsOn(database, "data.read", "data.write")
            .DependsOn(cache);

        // Act
        ApplicationModelManifest manifest = set.GetManifest();

        // Assert
        manifest.Name.ShouldBe(new GatewayName("east"));
        manifest.Parent.ShouldBe(new GatewayName("root"));
        manifest.Resources.Select(resource => resource.Name.Value).ShouldBe(["orders-db", "orders-cache", "orders-api"]);
        ApplicationResourceManifest ordersApi = manifest.Resources[2];
        ordersApi.Dependencies.Count.ShouldBe(2);
        ordersApi.Dependencies[0].ShouldSatisfyAllConditions(
            dependency => dependency.Gateway.ShouldBe(new GatewayName("east")),
            dependency => dependency.Name.Value.ShouldBe("orders-db"),
            dependency => dependency.Permissions.Select(permission => permission.Value).ShouldBe(["data.read", "data.write"]));
        ordersApi.Dependencies[1].Name.Value.ShouldBe("orders-cache");
        ordersApi.Dependencies[1].Permissions.ShouldBeEmpty();
    }
}

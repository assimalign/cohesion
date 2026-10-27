using System;
using System.Linq;

using Shouldly;
using Xunit;

namespace Assimalign.Cohesion.ApplicationModel.Tests;

public class ApplicationSetTests
{
    private const string DisplayPrefix = "Cohesion Test [ApplicationModel] - ApplicationSet: ";

    [Fact(DisplayName = DisplayPrefix + "A created set is the root of its hierarchy")]
    public void Create_WithName_ShouldBeRoot()
    {
        // Arrange & Act
        ApplicationBuilder root = Application.CreateBuilder("root", []);

        // Assert
        root.Name.ShouldBe(new GatewayName("root"));
        root.Parent.ShouldBeNull();
        root.Resources.ShouldBeEmpty();
        root.Gateways.ShouldBeEmpty();
    }

    [Fact(DisplayName = DisplayPrefix + "A set refuses a second resource with one provider and name")]
    public void AddResource_WithDuplicateInSet_ShouldThrow()
    {
        // Arrange
        ApplicationBuilder root = Application.CreateBuilder("root", []);
        root.AddResource("Web", "orders-api");

        // Act & Assert
        Should.Throw<InvalidOperationException>(() => root.AddResource("web", "orders-api"));
    }

    [Fact(DisplayName = DisplayPrefix + "A name may repeat under another provider or in another gateway")]
    public void AddResource_WithNameReusedElsewhere_ShouldSucceed()
    {
        // Arrange
        ApplicationBuilder root = Application.CreateBuilder("root", []);
        IApplicationSet east = root.AddGateway("east");
        IApplicationSet west = root.AddGateway("west");

        // Act
        root.AddResource("Web", "orders");
        root.AddResource("Database.Sql", "orders");
        east.AddResource("Web", "orders");
        west.AddResource("Web", "orders");

        // Assert
        root.Resources.Count.ShouldBe(2);
        east.Resources.ShouldHaveSingleItem().Set.ShouldBeSameAs(east);
        west.Resources.ShouldHaveSingleItem().Set.ShouldBeSameAs(west);
    }

    [Theory(DisplayName = DisplayPrefix + "Gateway names are unique across the whole hierarchy")]
    [InlineData("root")]
    [InlineData("east")]
    [InlineData("east-edge")]
    public void AddGateway_WithNameUsedAnywhereInHierarchy_ShouldThrow(string name)
    {
        // Arrange
        ApplicationBuilder root = Application.CreateBuilder("root", []);
        IApplicationSet east = root.AddGateway("east");
        IApplicationSet west = root.AddGateway("west");
        east.AddGateway("east-edge");

        // Act & Assert
        Should.Throw<InvalidOperationException>(() => west.AddGateway(name));
    }

    [Fact(DisplayName = DisplayPrefix + "The manifest projects the hierarchy in declaration order")]
    public void GetManifest_WithHierarchy_ShouldProjectInDeclarationOrder()
    {
        // Arrange
        ApplicationBuilder root = Application.CreateBuilder("root", []);
        IApplicationResourceDescriptor identity = root.AddResource("IdentityHub", "identity");
        IApplicationSet east = root.AddGateway("east");
        IApplicationResourceDescriptor database = east.AddResource("Database.Sql", "orders-db");
        east.AddResource("Web", "orders-api")
            .DependsOn(database, "data.read", "data.write")
            .DependsOn(identity);
        root.AddGateway("west");

        // Act
        ApplicationManifest manifest = root.GetManifest();

        // Assert
        manifest.Name.ShouldBe(new GatewayName("root"));
        manifest.Resources.ShouldHaveSingleItem().Name.ShouldBe(identity.Name);
        manifest.Gateways.Select(gateway => gateway.Name.Value).ShouldBe(["east", "west"]);
        ApplicationManifest eastManifest = manifest.Gateways[0];
        eastManifest.Resources.Select(resource => resource.Name.Value).ShouldBe(["orders-db", "orders-api"]);
        ApplicationResourceManifest ordersApi = eastManifest.Resources[1];
        ordersApi.Dependencies.Count.ShouldBe(2);
        ordersApi.Dependencies[0].ShouldSatisfyAllConditions(
            dependency => dependency.Gateway.ShouldBe(new GatewayName("east")),
            dependency => dependency.Name.Value.ShouldBe("orders-db"),
            dependency => dependency.Permissions.Select(permission => permission.Value).ShouldBe(["data.read", "data.write"]));
        ordersApi.Dependencies[1].Gateway.ShouldBe(new GatewayName("root"));
        ordersApi.Dependencies[1].Permissions.ShouldBeEmpty();
        manifest.Gateways[1].Resources.ShouldBeEmpty();
    }
}

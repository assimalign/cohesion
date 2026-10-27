using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;

using Shouldly;
using Xunit;

using Assimalign.Cohesion.Hosting.Resources;

namespace Assimalign.Cohesion.ApplicationModel.Tests;

public class ApplicationModelTests
{
    private const string DisplayPrefix = "Cohesion Test [ApplicationModel] - ApplicationModel: ";

    [Fact(DisplayName = DisplayPrefix + "A manifest that arrives as JSON becomes the same model the builder built")]
    public void Create_WithManifestFromJson_ShouldMatchBuiltModel()
    {
        // Arrange
        ApplicationBuilder builder = Application.CreateBuilder("root", []);
        IApplicationResourceDescriptor identity = builder.AddResource("IdentityHub", "identity");
        IApplicationSet east = builder.AddGateway("east");
        IApplicationResourceDescriptor database = east.AddResource("Database.Sql", "orders-db");
        east.AddResource("Web", "orders-api").DependsOn(database, "data.read", "data.write").DependsOn(identity);
        IApplicationModel built = builder.Build().Model;
        string json = JsonSerializer.Serialize(built.Manifest, ManifestJsonContext.Default.ApplicationManifest);

        // Act
        ApplicationModel received = ApplicationModel.Create(JsonSerializer.Deserialize(json, ManifestJsonContext.Default.ApplicationManifest)!);

        // Assert
        JsonSerializer.Serialize(received.Manifest, ManifestJsonContext.Default.ApplicationManifest).ShouldBe(json);
        ApplicationResourceManifest ordersApi = received.Manifest.Gateways[0].Resources[1];
        ordersApi.Dependencies.Select(dependency => dependency.Gateway.Value).ShouldBe(["east", "root"]);
        ordersApi.Dependencies[0].Permissions.Select(permission => permission.Value).ShouldBe(["data.read", "data.write"]);
    }

    [Fact(DisplayName = DisplayPrefix + "The model keeps a read-only copy of the lists it was given")]
    public void Create_ThenChangeSourceLists_ShouldNotChangeModel()
    {
        // Arrange
        var resources = new List<ApplicationResourceManifest> { new("Web", "orders-api", []) };
        var manifest = new ApplicationManifest("root", resources, []);

        // Act
        ApplicationModel model = ApplicationModel.Create(manifest);
        resources.Add(new ApplicationResourceManifest("Web", "customers-api", []));

        // Assert
        model.Manifest.Resources.ShouldHaveSingleItem().Name.Value.ShouldBe("orders-api");
        model.Manifest.Resources.ShouldNotBeSameAs(resources);
    }

    [Fact(DisplayName = DisplayPrefix + "Gateway names repeated anywhere in the manifest are refused")]
    public void Create_WithRepeatedGatewayName_ShouldThrow()
    {
        // Arrange
        var east = new ApplicationManifest("east", [], [new ApplicationManifest("root", [], [])]);

        // Act & Assert
        Should.Throw<ArgumentException>(() => ApplicationModel.Create(new ApplicationManifest("root", [], [east])));
    }

    [Fact(DisplayName = DisplayPrefix + "A gateway declaring one resource twice is refused")]
    public void Create_WithRepeatedResource_ShouldThrow()
    {
        // Arrange
        var manifest = new ApplicationManifest("root", [new("Web", "orders-api", []), new("web", "orders-api", [])], []);

        // Act & Assert
        Should.Throw<ArgumentException>(() => ApplicationModel.Create(manifest));
    }

    [Fact(DisplayName = DisplayPrefix + "A dependency on a resource the manifest does not declare is refused")]
    public void Create_WithUnknownDependency_ShouldThrow()
    {
        // Arrange
        var dependency = new ApplicationResourceDependency("west", "Database.Sql", "orders-db", []);
        var manifest = new ApplicationManifest("root", [new("Web", "orders-api", [dependency])], []);

        // Act & Assert
        Should.Throw<ArgumentException>(() => ApplicationModel.Create(manifest));
    }

    [Fact(DisplayName = DisplayPrefix + "A resource depending on itself is refused")]
    public void Create_WithSelfDependency_ShouldThrow()
    {
        // Arrange
        var dependency = new ApplicationResourceDependency("root", "Web", "orders-api", []);
        var manifest = new ApplicationManifest("root", [new("Web", "orders-api", [dependency])], []);

        // Act & Assert
        Should.Throw<ArgumentException>(() => ApplicationModel.Create(manifest));
    }

    [Fact(DisplayName = DisplayPrefix + "Repeated dependencies and repeated permissions are refused")]
    public void Create_WithRepeatedDependencyOrPermission_ShouldThrow()
    {
        // Arrange
        ApplicationResourceManifest database = new("Database.Sql", "orders-db", []);
        var repeatedDependency = new ApplicationManifest("root", [database, new("Web", "orders-api", [
            new ApplicationResourceDependency("root", "Database.Sql", "orders-db", []),
            new ApplicationResourceDependency("root", "Database.Sql", "orders-db", ["data.read"])])], []);
        var repeatedPermission = new ApplicationManifest("root", [database, new("Web", "orders-api", [
            new ApplicationResourceDependency("root", "Database.Sql", "orders-db", ["data.read", "data.read"])])], []);

        // Act & Assert
        Should.Throw<ArgumentException>(() => ApplicationModel.Create(repeatedDependency));
        Should.Throw<ArgumentException>(() => ApplicationModel.Create(repeatedPermission));
    }

    [Fact(DisplayName = DisplayPrefix + "A child gateway's model keeps the whole hierarchy and deploys the child's part")]
    public void Create_ForChildGateway_ShouldSelectItsPart()
    {
        // Arrange
        ApplicationBuilder builder = Application.CreateBuilder("root", []);
        IApplicationSet east = builder.AddGateway("east");
        east.AddResource("Web", "orders-api");
        east.AddGateway("east-edge");
        ApplicationManifest manifest = builder.GetManifest();

        // Act
        IApplicationModel root = ApplicationModel.Create(manifest);
        IApplicationModel child = ApplicationModel.Create(manifest, "east");

        // Assert
        root.Role.ShouldBe(GatewayRole.Root);
        root.Deployment.Name.Value.ShouldBe("root");
        child.Role.ShouldBe(GatewayRole.Composite);
        child.Name.Value.ShouldBe("east");
        child.Id.ShouldBe(GatewayId.FromName("east"));
        child.Manifest.Name.Value.ShouldBe("root");
        child.Deployment.Resources.ShouldHaveSingleItem().Name.Value.ShouldBe("orders-api");
        child.Deployment.Gateways.ShouldHaveSingleItem().Name.Value.ShouldBe("east-edge");
    }

    [Fact(DisplayName = DisplayPrefix + "A model for a gateway the manifest does not declare is refused")]
    public void Create_ForUnknownGateway_ShouldThrow()
    {
        // Arrange
        var manifest = new ApplicationManifest("root", [], []);

        // Act & Assert
        Should.Throw<ArgumentException>(() => ApplicationModel.Create(manifest, "east"));
    }

    [Fact(DisplayName = DisplayPrefix + "Lists that JSON left null are refused, not dereferenced")]
    public void Create_WithNullListsFromJson_ShouldThrow()
    {
        // Arrange
        ApplicationManifest manifest = JsonSerializer.Deserialize("""{"Name":"root","Resources":null,"Gateways":[]}""", ManifestJsonContext.Default.ApplicationManifest)!;

        // Act & Assert
        Should.Throw<ArgumentException>(() => ApplicationModel.Create(manifest));
    }
}

using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;

using Shouldly;
using Xunit;

namespace Assimalign.Cohesion.ApplicationModel.Definition.Tests;

public class ApplicationModelTests
{
    private const string DisplayPrefix = "Cohesion Test [ApplicationModel] - ApplicationModel: ";

    [Fact(DisplayName = DisplayPrefix + "A manifest that arrives as JSON becomes the same model the builder built")]
    public void Create_WithManifestFromJson_ShouldMatchBuiltModel()
    {
        // Arrange
        IApplicationModelBuilder builder = ApplicationModel.CreateBuilder("east");
        IApplicationModelParent root = builder.UseParent("root");
        IApplicationResourceDefinition database = builder.AddResource("Database.Sql", "orders-db");
        builder.AddResource("Web", "orders-api")
            .DependsOn(database, "data.read", "data.write")
            .WithSetting("Identity:Endpoint", root.Endpoint("IdentityHub", "identity"));
        IApplicationModel built = builder.Build();
        string json = JsonSerializer.Serialize(built.Manifest, ManifestJsonContext.Default.ApplicationModelManifest);

        // Act
        ApplicationModel received = ApplicationModel.Create(JsonSerializer.Deserialize(json, ManifestJsonContext.Default.ApplicationModelManifest)!);

        // Assert
        JsonSerializer.Serialize(received.Manifest, ManifestJsonContext.Default.ApplicationModelManifest).ShouldBe(json);
        received.Parent.ShouldBe(new GatewayName("root"));
        ApplicationResourceManifest ordersApi = received.Manifest.Resources[1];
        ordersApi.Dependencies.ShouldHaveSingleItem().Permissions.Select(permission => permission.Value).ShouldBe(["data.read", "data.write"]);
        ordersApi.Settings.ShouldHaveSingleItem().Output.ShouldBe(new ApplicationResourceOutput("root", "IdentityHub", "identity", OutputName.Endpoint));
    }

    [Fact(DisplayName = DisplayPrefix + "The model keeps a read-only copy of the lists it was given")]
    public void Create_ThenChangeSourceLists_ShouldNotChangeModel()
    {
        // Arrange
        var resources = new List<ApplicationResourceManifest> { new("Web", "orders-api", [], [], []) };
        var manifest = new ApplicationModelManifest("root", null, resources);

        // Act
        ApplicationModel model = ApplicationModel.Create(manifest);
        resources.Add(new ApplicationResourceManifest("Web", "customers-api", [], [], []));

        // Assert
        model.Manifest.Resources.ShouldHaveSingleItem().Name.Value.ShouldBe("orders-api");
        model.Manifest.Resources.ShouldNotBeSameAs(resources);
    }

    [Fact(DisplayName = DisplayPrefix + "A gateway naming itself as its parent is refused")]
    public void Create_WithParentNamingItself_ShouldThrow()
    {
        // Act & Assert
        Should.Throw<ArgumentException>(() => ApplicationModel.Create(new ApplicationModelManifest("east", "east", [])));
    }

    [Fact(DisplayName = DisplayPrefix + "A gateway declaring one resource twice is refused")]
    public void Create_WithRepeatedResource_ShouldThrow()
    {
        // Arrange
        var manifest = new ApplicationModelManifest("root", null, [new("Web", "orders-api", [], [], []), new("web", "orders-api", [], [], [])]);

        // Act & Assert
        Should.Throw<ArgumentException>(() => ApplicationModel.Create(manifest));
    }

    [Fact(DisplayName = DisplayPrefix + "A dependency on an undeclared resource, or on another gateway's, is refused")]
    public void Create_WithUnknownDependency_ShouldThrow()
    {
        // Arrange: a gateway orders only its own resources, so even its parent's are refused.
        var undeclared = new ApplicationModelManifest("root", null, [
            new("Web", "orders-api", [new ApplicationResourceDependency("root", "Database.Sql", "orders-db", [])], [], [])]);
        var parents = new ApplicationModelManifest("east", "root", [
            new("Web", "orders-api", [new ApplicationResourceDependency("root", "IdentityHub", "identity", [])], [], [])]);

        // Act & Assert
        Should.Throw<ArgumentException>(() => ApplicationModel.Create(undeclared));
        Should.Throw<ArgumentException>(() => ApplicationModel.Create(parents));
    }

    [Fact(DisplayName = DisplayPrefix + "A resource depending on itself is refused")]
    public void Create_WithSelfDependency_ShouldThrow()
    {
        // Arrange
        var dependency = new ApplicationResourceDependency("root", "Web", "orders-api", []);
        var manifest = new ApplicationModelManifest("root", null, [new("Web", "orders-api", [dependency], [], [])]);

        // Act & Assert
        Should.Throw<ArgumentException>(() => ApplicationModel.Create(manifest));
    }

    [Fact(DisplayName = DisplayPrefix + "Dependencies that form a cycle are refused, naming the cycle")]
    public void Create_WithDependencyCycle_ShouldThrowNamingTheCycle()
    {
        // Arrange: orders-api and orders-db depend on each other.
        var manifest = new ApplicationModelManifest(
            "east",
            null,
            [
                new("Web", "orders-api", [new ApplicationResourceDependency("east", "Database.Sql", "orders-db", [])], [], []),
                new("Database.Sql", "orders-db", [new ApplicationResourceDependency("east", "Web", "orders-api", ["data.read"])], [], []),
            ]);

        // Act
        ArgumentException exception = Should.Throw<ArgumentException>(() => ApplicationModel.Create(manifest));

        // Assert
        exception.Message.ShouldContain("east/Web/orders-api -> east/Database.Sql/orders-db -> east/Web/orders-api", Case.Sensitive);
    }

    [Fact(DisplayName = DisplayPrefix + "Repeated dependencies and repeated permissions are refused")]
    public void Create_WithRepeatedDependencyOrPermission_ShouldThrow()
    {
        // Arrange
        ApplicationResourceManifest database = new("Database.Sql", "orders-db", [], [], []);
        var repeatedDependency = new ApplicationModelManifest("root", null, [database, new("Web", "orders-api", [
            new ApplicationResourceDependency("root", "Database.Sql", "orders-db", []),
            new ApplicationResourceDependency("root", "Database.Sql", "orders-db", ["data.read"])], [], [])]);
        var repeatedPermission = new ApplicationModelManifest("root", null, [database, new("Web", "orders-api", [
            new ApplicationResourceDependency("root", "Database.Sql", "orders-db", ["data.read", "data.read"])], [], [])]);

        // Act & Assert
        Should.Throw<ArgumentException>(() => ApplicationModel.Create(repeatedDependency));
        Should.Throw<ArgumentException>(() => ApplicationModel.Create(repeatedPermission));
    }

    [Fact(DisplayName = DisplayPrefix + "A setting may use an output of the parent's resources, which orders nothing")]
    public void Create_WithOutputOfParent_ShouldAcceptIt()
    {
        // Arrange
        var output = new ApplicationResourceOutput("root", "IdentityHub", "identity", OutputName.Endpoint);
        var manifest = new ApplicationModelManifest("east", "root", [new("Web", "orders-api", [], [new ApplicationResourceSetting("Identity:Endpoint", null, output)], [])]);

        // Act
        ApplicationModel model = ApplicationModel.Create(manifest);

        // Assert
        model.Role.ShouldBe(ApplicationGatewayRole.Composite);
        model.Manifest.Resources.ShouldHaveSingleItem().ShouldSatisfyAllConditions(
            resource => resource.Dependencies.ShouldBeEmpty(),
            resource => resource.Settings.ShouldHaveSingleItem().Output.ShouldBe(output));
    }

    [Theory(DisplayName = DisplayPrefix + "A setting may not use an output of a gateway that is neither this one nor its parent")]
    [InlineData(null)]
    [InlineData("west")]
    public void Create_WithOutputOfAnotherGateway_ShouldThrow(string? parent)
    {
        // Arrange
        var output = new ApplicationResourceOutput("root", "IdentityHub", "identity", OutputName.Endpoint);
        var manifest = new ApplicationModelManifest(
            "east",
            parent is null ? (GatewayName?)null : new GatewayName(parent),
            [new("Web", "orders-api", [], [new ApplicationResourceSetting("Identity:Endpoint", null, output)], [])]);

        // Act
        ArgumentException exception = Should.Throw<ArgumentException>(() => ApplicationModel.Create(manifest));

        // Assert
        exception.Message.ShouldContain("'root'", Case.Sensitive);
    }

    [Fact(DisplayName = DisplayPrefix + "Lists that JSON left null are refused, not dereferenced")]
    public void Create_WithNullListsFromJson_ShouldThrow()
    {
        // Arrange
        ApplicationModelManifest manifest = JsonSerializer.Deserialize("""{"Name":"root","Parent":null,"Resources":null}""", ManifestJsonContext.Default.ApplicationModelManifest)!;

        // Act & Assert
        Should.Throw<ArgumentException>(() => ApplicationModel.Create(manifest));
    }
}

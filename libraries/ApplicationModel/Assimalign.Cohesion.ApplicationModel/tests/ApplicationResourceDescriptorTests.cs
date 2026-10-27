using System;
using System.Linq;

using Shouldly;
using Xunit;

namespace Assimalign.Cohesion.ApplicationModel.Tests;

public class ApplicationResourceDescriptorTests
{
    private const string DisplayPrefix = "Cohesion Test [ApplicationModel] - ApplicationResourceDescriptor: ";

    [Fact(DisplayName = DisplayPrefix + "Declaring a dependency again adds its new permissions once")]
    public void DependsOn_WithSameResourceTwice_ShouldMergePermissions()
    {
        // Arrange
        ApplicationBuilder root = Application.CreateBuilder("root", []);
        IApplicationResourceDescriptor database = root.AddResource("Database.Sql", "orders-db");
        IApplicationResourceDescriptor api = root.AddResource("Web", "orders-api");

        // Act
        api.DependsOn(database, "data.read").DependsOn(database, "data.write", "data.read");

        // Assert
        ApplicationResourceDependency dependency = api.Dependencies.ShouldHaveSingleItem();
        dependency.Permissions.Select(permission => permission.Value).ShouldBe(["data.read", "data.write"]);
    }

    [Fact(DisplayName = DisplayPrefix + "A dependency names the gateway whose set declares the resource")]
    public void DependsOn_WithResourceInAnotherGateway_ShouldRecordThatGateway()
    {
        // Arrange
        ApplicationBuilder root = Application.CreateBuilder("root", []);
        IApplicationResourceDescriptor identity = root.AddResource("IdentityHub", "identity");
        IApplicationResourceDescriptor api = root.AddGateway("east").AddResource("Web", "orders-api");

        // Act
        api.DependsOn(identity, "tokens.issue");

        // Assert
        ApplicationResourceDependency dependency = api.Dependencies.ShouldHaveSingleItem();
        dependency.Gateway.ShouldBe(root.Name);
        dependency.Provider.ShouldBe(identity.Provider);
        dependency.Name.ShouldBe(identity.Name);
    }

    [Fact(DisplayName = DisplayPrefix + "A resource cannot depend on itself")]
    public void DependsOn_WithItself_ShouldThrow()
    {
        // Arrange
        IApplicationResourceDescriptor api = Application.CreateBuilder("root", []).AddResource("Web", "orders-api");

        // Act & Assert
        Should.Throw<ArgumentException>(() => api.DependsOn(api));
    }

    [Fact(DisplayName = DisplayPrefix + "A resource of another hierarchy is refused")]
    public void DependsOn_WithResourceOfAnotherHierarchy_ShouldThrow()
    {
        // Arrange
        IApplicationResourceDescriptor api = Application.CreateBuilder("contoso", []).AddResource("Web", "orders-api");
        IApplicationResourceDescriptor database = Application.CreateBuilder("fabrikam", []).AddResource("Database.Sql", "orders-db");

        // Act & Assert
        Should.Throw<ArgumentException>(() => api.DependsOn(database, "data.read"));
        api.Dependencies.ShouldBeEmpty();
    }

    [Fact(DisplayName = DisplayPrefix + "A default permission is refused")]
    public void DependsOn_WithDefaultPermission_ShouldThrow()
    {
        // Arrange
        ApplicationBuilder root = Application.CreateBuilder("root", []);
        IApplicationResourceDescriptor database = root.AddResource("Database.Sql", "orders-db");
        IApplicationResourceDescriptor api = root.AddResource("Web", "orders-api");

        // Act & Assert
        Should.Throw<ArgumentException>(() => api.DependsOn(database, "data.read", default));
        api.Dependencies.ShouldBeEmpty();
    }
}

using System;
using System.Linq;

using Shouldly;
using Xunit;

namespace Assimalign.Cohesion.ApplicationModel.Definition.Tests;

public class ApplicationResourceDefinitionTests
{
    private const string DisplayPrefix = "Cohesion Test [ApplicationModel] - ApplicationResourceDefinition: ";

    [Fact(DisplayName = DisplayPrefix + "Declaring a dependency again adds its new permissions once")]
    public void DependsOn_WithSameResourceTwice_ShouldMergePermissions()
    {
        // Arrange
        ApplicationBuilder root = Application.CreateBuilder("root", []);
        IApplicationResourceDefinition database = root.AddResource("Database.Sql", "orders-db");
        IApplicationResourceDefinition api = root.AddResource("Web", "orders-api");

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
        IApplicationResourceDefinition identity = root.AddResource("IdentityHub", "identity");
        IApplicationResourceDefinition api = root.AddGateway("east").AddResource("Web", "orders-api");

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
        IApplicationResourceDefinition api = Application.CreateBuilder("root", []).AddResource("Web", "orders-api");

        // Act & Assert
        Should.Throw<ArgumentException>(() => api.DependsOn(api));
    }

    [Fact(DisplayName = DisplayPrefix + "A dependency that would close a cycle is refused, naming it, and not recorded")]
    public void DependsOn_ClosingCycle_ShouldThrowAndRecordNothing()
    {
        // Arrange
        ApplicationBuilder root = Application.CreateBuilder("root", []);
        IApplicationResourceDefinition web = root.AddResource("Web", "web");
        IApplicationResourceDefinition api = root.AddResource("Web", "api");
        IApplicationResourceDefinition database = root.AddGateway("east").AddResource("Database.Sql", "db");
        web.DependsOn(api);
        api.DependsOn(database, "data.read");

        // Act
        ArgumentException exception = Should.Throw<ArgumentException>(() => database.DependsOn(web));

        // Assert
        exception.Message.ShouldContain("east/Database.Sql/db -> root/Web/web -> root/Web/api -> east/Database.Sql/db", Case.Sensitive);
        database.Dependencies.ShouldBeEmpty();
        Should.NotThrow(() => root.Build());
    }

    [Fact(DisplayName = DisplayPrefix + "A resource of another hierarchy is refused")]
    public void DependsOn_WithResourceOfAnotherHierarchy_ShouldThrow()
    {
        // Arrange
        IApplicationResourceDefinition api = Application.CreateBuilder("contoso", []).AddResource("Web", "orders-api");
        IApplicationResourceDefinition database = Application.CreateBuilder("fabrikam", []).AddResource("Database.Sql", "orders-db");

        // Act & Assert
        Should.Throw<ArgumentException>(() => api.DependsOn(database, "data.read"));
        api.Dependencies.ShouldBeEmpty();
    }

    [Fact(DisplayName = DisplayPrefix + "A default permission is refused")]
    public void DependsOn_WithDefaultPermission_ShouldThrow()
    {
        // Arrange
        ApplicationBuilder root = Application.CreateBuilder("root", []);
        IApplicationResourceDefinition database = root.AddResource("Database.Sql", "orders-db");
        IApplicationResourceDefinition api = root.AddResource("Web", "orders-api");

        // Act & Assert
        Should.Throw<ArgumentException>(() => api.DependsOn(database, "data.read", default));
        api.Dependencies.ShouldBeEmpty();
    }
}

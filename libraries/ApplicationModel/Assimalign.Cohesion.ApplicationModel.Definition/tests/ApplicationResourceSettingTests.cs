using System;
using System.Linq;

using Shouldly;
using Xunit;

namespace Assimalign.Cohesion.ApplicationModel.Definition.Tests;

public class ApplicationResourceSettingTests
{
    private const string DisplayPrefix = "Cohesion Test [ApplicationModel] - ApplicationResourceSetting: ";

    [Theory(DisplayName = DisplayPrefix + "Setting names are colon-separated configuration key segments")]
    [InlineData("Endpoint", true)]
    [InlineData("ConfigurationStore:Endpoint", true)]
    [InlineData("Logging:LogLevel:Microsoft.Hosting", true)]
    [InlineData("Cors:Origins:0", true)]
    [InlineData("orders-api_settings", true)]
    [InlineData(":Endpoint", false)]
    [InlineData("Endpoint:", false)]
    [InlineData("Store::Endpoint", false)]
    [InlineData("Store Endpoint", false)]
    [InlineData("Store/Endpoint", false)]
    [InlineData("", false)]
    public void SettingName_TryCreate_ShouldFollowItsGrammar(string value, bool expected)
    {
        // Act & Assert
        SettingName.TryCreate(value, out _).ShouldBe(expected);
    }

    [Fact(DisplayName = DisplayPrefix + "Setting names compare case-insensitively, as configuration keys do")]
    public void SettingName_WithOtherCase_ShouldBeEqual()
    {
        // Arrange
        SettingName name = "ConfigurationStore:Endpoint";
        SettingName other = "configurationstore:endpoint";

        // Act & Assert
        name.Equals(other).ShouldBeTrue();
        name.GetHashCode().ShouldBe(other.GetHashCode());
    }

    [Fact(DisplayName = DisplayPrefix + "A value given in the model is recorded in the manifest")]
    public void WithSetting_WithValue_ShouldRecordIt()
    {
        // Arrange
        IApplicationModelBuilder root = ApplicationModel.CreateBuilder("root");
        IApplicationResourceDefinition api = root.AddResource("Web", "orders-api");

        // Act
        api.WithSetting("Logging:LogLevel:Default", "Debug");

        // Assert
        ApplicationResourceSetting setting = root.Build().Manifest.Resources.ShouldHaveSingleItem().Settings.ShouldHaveSingleItem();
        setting.ShouldBe(new ApplicationResourceSetting("Logging:LogLevel:Default", "Debug", null));
        api.Dependencies.ShouldBeEmpty();
    }

    [Fact(DisplayName = DisplayPrefix + "A value from another resource's output makes the resource depend on it")]
    public void WithSetting_WithOutput_ShouldDependOnTheOutputsResource()
    {
        // Arrange
        IApplicationModelBuilder root = ApplicationModel.CreateBuilder("root");
        IApplicationResourceDefinition store = root.AddResource("ConfigurationStore", "settings");
        IApplicationResourceDefinition api = root.AddResource("Web", "orders-api");

        // Act
        api.WithSetting("ConfigurationStore:Endpoint", store.Endpoint);

        // Assert
        api.Settings.ShouldHaveSingleItem().ShouldBe(new ApplicationResourceSetting("ConfigurationStore:Endpoint", null, store.Endpoint));
        ApplicationResourceDependency dependency = api.Dependencies.ShouldHaveSingleItem();
        (dependency.Gateway.Value, dependency.Provider.Value, dependency.Name.Value).ShouldBe(("root", "ConfigurationStore", "settings"));
        dependency.Permissions.ShouldBeEmpty();
    }

    [Fact(DisplayName = DisplayPrefix + "A value from an output keeps the permissions the dependency already grants")]
    public void WithSetting_WithOutputOfDependency_ShouldKeepItsPermissions()
    {
        // Arrange
        IApplicationModelBuilder root = ApplicationModel.CreateBuilder("root");
        IApplicationResourceDefinition database = root.AddResource("Database.Sql", "orders-db");
        IApplicationResourceDefinition api = root.AddResource("Web", "orders-api").DependsOn(database, "data.read");

        // Act
        api.WithSetting("Database:Endpoint", database.Endpoint);

        // Assert
        api.Dependencies.ShouldHaveSingleItem().Permissions.Select(permission => permission.Value).ShouldBe(["data.read"]);
    }

    [Fact(DisplayName = DisplayPrefix + "Setting a name again replaces its value in place, whatever its case")]
    public void WithSetting_WithSameName_ShouldReplaceTheValue()
    {
        // Arrange
        IApplicationModelBuilder root = ApplicationModel.CreateBuilder("root");
        IApplicationResourceDefinition database = root.AddResource("Database.Sql", "orders-db");
        IApplicationResourceDefinition api = root.AddResource("Web", "orders-api");

        // Act
        api.WithSetting("Database:Endpoint", "http://localhost:5432")
            .WithSetting("Feature:Mode", "fast")
            .WithSetting("database:endpoint", database.Endpoint);

        // Assert
        api.Settings.Select(setting => setting.Name.Value).ShouldBe(["database:endpoint", "Feature:Mode"]);
        api.Settings[0].Output.ShouldBe(database.Endpoint);
        api.Settings[0].Value.ShouldBeNull();
    }

    [Fact(DisplayName = DisplayPrefix + "A resource's own output cannot be one of its settings")]
    public void WithSetting_WithOwnOutput_ShouldThrow()
    {
        // Arrange
        IApplicationResourceDefinition api = ApplicationModel.CreateBuilder("root").AddResource("Web", "orders-api");

        // Act & Assert
        Should.Throw<ArgumentException>(() => api.WithSetting("Self:Endpoint", api.Endpoint));
        api.Settings.ShouldBeEmpty();
    }

    [Fact(DisplayName = DisplayPrefix + "An output of a resource this gateway does not declare is refused")]
    public void WithSetting_WithUndeclaredOutput_ShouldThrow()
    {
        // Arrange
        IApplicationModelBuilder root = ApplicationModel.CreateBuilder("root");
        IApplicationResourceDefinition api = root.AddResource("Web", "orders-api");
        var output = new ApplicationResourceOutput("root", "Database.Sql", "orders-db", OutputName.Endpoint);

        // Act & Assert
        Should.Throw<ArgumentException>(() => api.WithSetting("Database:Endpoint", output));
        api.Dependencies.ShouldBeEmpty();
    }

    [Fact(DisplayName = DisplayPrefix + "A value from the parent's output is recorded without a dependency")]
    public void WithSetting_WithParentOutput_ShouldRecordItWithoutDependency()
    {
        // Arrange
        IApplicationModelBuilder east = ApplicationModel.CreateBuilder("east");
        IApplicationParent root = east.UseParent("root");
        IApplicationResourceDefinition api = east.AddResource("Web", "orders-api");

        // Act
        api.WithSetting("Identity:Endpoint", root.Endpoint("IdentityHub", "identity"));

        // Assert
        api.Settings.ShouldHaveSingleItem().Output.ShouldBe(new ApplicationResourceOutput("root", "IdentityHub", "identity", OutputName.Endpoint));
        api.Dependencies.ShouldBeEmpty();
        east.Build().Manifest.Resources.ShouldHaveSingleItem().Dependencies.ShouldBeEmpty();
    }

    [Fact(DisplayName = DisplayPrefix + "An output whose resource depends on this one is refused, and nothing is recorded")]
    public void WithSetting_WithOutputClosingCycle_ShouldThrowAndRecordNothing()
    {
        // Arrange: the database already depends on the api.
        IApplicationModelBuilder root = ApplicationModel.CreateBuilder("root");
        IApplicationResourceDefinition api = root.AddResource("Web", "orders-api");
        IApplicationResourceDefinition database = root.AddResource("Database.Sql", "orders-db").DependsOn(api);

        // Act
        ArgumentException exception = Should.Throw<ArgumentException>(() => api.WithSetting("Database:Endpoint", database.Endpoint));

        // Assert
        exception.Message.ShouldContain("root/Web/orders-api -> root/Database.Sql/orders-db -> root/Web/orders-api", Case.Sensitive);
        api.Settings.ShouldBeEmpty();
        api.Dependencies.ShouldBeEmpty();
    }

    [Fact(DisplayName = DisplayPrefix + "Settings cannot change once the application is built")]
    public void WithSetting_AfterBuild_ShouldThrow()
    {
        // Arrange
        IApplicationModelBuilder root = ApplicationModel.CreateBuilder("root");
        IApplicationResourceDefinition api = root.AddResource("Web", "orders-api");
        root.Build();

        // Act & Assert
        Should.Throw<InvalidOperationException>(() => api.WithSetting("Feature:Mode", "fast"));
    }

    [Fact(DisplayName = DisplayPrefix + "A manifest setting whose output's resource is not a dependency is refused")]
    public void Create_WithOutputOfResourceNotDependedOn_ShouldThrow()
    {
        // Arrange
        var output = new ApplicationResourceOutput("root", "Database.Sql", "orders-db", OutputName.Endpoint);
        var manifest = new ApplicationManifest(
            "root",
            null,
            [new("Database.Sql", "orders-db", [], [], []), new("Web", "orders-api", [], [new ApplicationResourceSetting("Database:Endpoint", null, output)], [])]);

        // Act
        ArgumentException exception = Should.Throw<ArgumentException>(() => ApplicationModel.Create(manifest));

        // Assert
        exception.Message.ShouldContain("which the resource does not depend on", Case.Sensitive);
    }

    [Fact(DisplayName = DisplayPrefix + "Manifest settings need one unique name and exactly one of a value and an output")]
    public void Create_WithInvalidSettings_ShouldThrow()
    {
        // Arrange
        var output = new ApplicationResourceOutput("root", "Database.Sql", "orders-db", OutputName.Endpoint);
        var dependency = new ApplicationResourceDependency("root", "Database.Sql", "orders-db", []);
        ApplicationResourceManifest database = new("Database.Sql", "orders-db", [], [], []);

        ApplicationManifest With(params ApplicationResourceSetting[] settings)
        {
            return new ApplicationManifest("root", null, [database, new("Web", "orders-api", [dependency], settings, [])]);
        }

        // Act & Assert
        Should.NotThrow(() => ApplicationModel.Create(With(new("Database:Endpoint", null, output), new("Feature:Mode", "fast", null))));
        Should.Throw<ArgumentException>(() => ApplicationModel.Create(With(new("Feature:Mode", "fast", null), new("feature:mode", "safe", null))));
        Should.Throw<ArgumentException>(() => ApplicationModel.Create(With(new ApplicationResourceSetting("Feature:Mode", null, null))));
        Should.Throw<ArgumentException>(() => ApplicationModel.Create(With(new ApplicationResourceSetting("Database:Endpoint", "http://localhost", output))));
        Should.Throw<ArgumentException>(() => ApplicationModel.Create(With(new ApplicationResourceSetting("Self:Endpoint", null, new ApplicationResourceOutput("root", "Web", "orders-api", OutputName.Endpoint)))));
    }
}

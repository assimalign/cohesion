using System;

using Shouldly;
using Xunit;

namespace Assimalign.Cohesion.ApplicationModel.Definition.Tests;

public class ApplicationResourceOutputTests
{
    private const string DisplayPrefix = "Cohesion Test [ApplicationModel] - Resource outputs: ";

    [Theory(DisplayName = DisplayPrefix + "Output names are lowercase words joined by hyphens")]
    [InlineData("endpoint", true)]
    [InlineData("admin-endpoint", true)]
    [InlineData("port2", true)]
    [InlineData("Endpoint", false)]
    [InlineData("admin--endpoint", false)]
    [InlineData("-endpoint", false)]
    [InlineData("endpoint-", false)]
    [InlineData("connection.string", false)]
    [InlineData("", false)]
    public void OutputName_TryCreate_ShouldFollowItsGrammar(string value, bool expected)
    {
        // Act & Assert
        OutputName.TryCreate(value, out _).ShouldBe(expected);
    }

    [Fact(DisplayName = DisplayPrefix + "A definition's output placeholder names its gateway, resource, and output")]
    public void Output_WithName_ShouldReferenceTheResource()
    {
        // Arrange
        IApplicationResourceDefinition store = ApplicationModel.CreateBuilder("east").AddResource("ConfigurationStore", "settings");

        // Act
        ApplicationResourceOutput endpoint = store.Endpoint;
        ApplicationResourceOutput adminEndpoint = store.Output("admin-endpoint");

        // Assert
        endpoint.ShouldBe(new ApplicationResourceOutput("east", "ConfigurationStore", "settings", OutputName.Endpoint));
        adminEndpoint.Output.ShouldBe((OutputName)"admin-endpoint");
    }

    [Fact(DisplayName = DisplayPrefix + "An output placeholder needs a name")]
    public void Output_WithDefaultName_ShouldThrow()
    {
        // Arrange
        IApplicationResourceDefinition store = ApplicationModel.CreateBuilder("root").AddResource("ConfigurationStore", "settings");

        // Act & Assert
        Should.Throw<ArgumentException>(() => store.Output(default));
    }

    [Fact(DisplayName = DisplayPrefix + "The parent's output placeholder names the parent, its resource, and the output")]
    public void ParentOutput_WithNames_ShouldReferenceTheParentsResource()
    {
        // Arrange
        IApplicationParent root = ApplicationModel.CreateBuilder("east").UseParent("root");

        // Act
        ApplicationResourceOutput endpoint = root.Endpoint("IdentityHub", "identity");
        ApplicationResourceOutput adminEndpoint = root.Output("IdentityHub", "identity", "admin-endpoint");

        // Assert
        endpoint.ShouldBe(new ApplicationResourceOutput("root", "IdentityHub", "identity", OutputName.Endpoint));
        adminEndpoint.ShouldBe(new ApplicationResourceOutput("root", "IdentityHub", "identity", "admin-endpoint"));
    }

    [Fact(DisplayName = DisplayPrefix + "The parent's output placeholder needs a provider, a name, and an output")]
    public void ParentOutput_WithDefaultNames_ShouldThrow()
    {
        // Arrange
        IApplicationParent root = ApplicationModel.CreateBuilder("east").UseParent("root");

        // Act & Assert
        Should.Throw<ArgumentException>(() => root.Output(default, "identity", OutputName.Endpoint));
        Should.Throw<ArgumentException>(() => root.Output("IdentityHub", default, OutputName.Endpoint));
        Should.Throw<ArgumentException>(() => root.Output("IdentityHub", "identity", default));
    }
}

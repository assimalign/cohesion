using System;
using System.Collections.Generic;

using Shouldly;
using Xunit;

namespace Assimalign.Cohesion.ApplicationModel.Tests;

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

    [Fact(DisplayName = DisplayPrefix + "A descriptor's output placeholder names its gateway, resource, and output")]
    public void Output_WithName_ShouldReferenceTheResource()
    {
        // Arrange
        ApplicationBuilder root = Application.CreateBuilder("root", []);
        IApplicationSet east = root.AddGateway("east");
        IApplicationResourceDescriptor store = east.AddResource("ConfigurationStore", "settings");

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
        IApplicationResourceDescriptor store = Application.CreateBuilder("root", []).AddResource("ConfigurationStore", "settings");

        // Act & Assert
        Should.Throw<ArgumentException>(() => store.Output(default));
    }

    [Fact(DisplayName = DisplayPrefix + "Setting outputs reports whether any was added, changed, or removed")]
    public void SetOutputs_ShouldReportChanges()
    {
        // Arrange
        ApplicationResource resource = ApplicationResource.Create(TestManifests.OrdersApi);

        // Act
        bool added = resource.SetOutputs(Endpoint("http://orders-api.root.svc"));
        bool same = resource.SetOutputs(Endpoint("http://orders-api.root.svc"));
        bool moved = resource.SetOutputs(Endpoint("http://10.0.0.7:8080"));
        string current = resource.Outputs[OutputName.Endpoint];
        bool removed = resource.SetOutputs(new Dictionary<OutputName, string>());

        // Assert
        added.ShouldBeTrue();
        same.ShouldBeFalse();
        moved.ShouldBeTrue();
        current.ShouldBe("http://10.0.0.7:8080");
        removed.ShouldBeTrue();
        resource.Outputs.ShouldBeEmpty();
    }

    [Fact(DisplayName = DisplayPrefix + "An output without a value is refused and changes nothing")]
    public void SetOutputs_WithoutValue_ShouldThrow()
    {
        // Arrange
        ApplicationResource resource = ApplicationResource.Create(TestManifests.OrdersApi, outputs: Endpoint("http://orders-api.root.svc"));

        // Act & Assert
        Should.Throw<ArgumentException>(() => resource.SetOutputs(Endpoint(null!)));
        resource.Outputs[OutputName.Endpoint].ShouldBe("http://orders-api.root.svc");
    }

    private static Dictionary<OutputName, string> Endpoint(string value)
    {
        return new Dictionary<OutputName, string> { [OutputName.Endpoint] = value };
    }
}

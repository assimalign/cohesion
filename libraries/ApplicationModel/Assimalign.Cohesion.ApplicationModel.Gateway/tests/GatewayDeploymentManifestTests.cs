using System;
using System.Linq;
using System.Text;
using System.Text.Json;

using Shouldly;
using Xunit;

namespace Assimalign.Cohesion.ApplicationModel.Gateway.Tests;

public class GatewayDeploymentManifestTests
{
    private const string DisplayPrefix = "Cohesion Test [ApplicationModel] - GatewayDeploymentManifest: ";

    [Fact(DisplayName = DisplayPrefix + "A deployment manifest round-trips as camel-case JSON with its infrastructure settings")]
    public void ToUtf8Json_ThenParse_ShouldRoundTrip()
    {
        // Arrange
        using JsonDocument settings = JsonDocument.Parse("""{"namespace":"east"}""");
        var manifest = new GatewayDeploymentManifest("east", CreateManifest(), null, settings.RootElement.Clone());

        // Act
        byte[] json = manifest.ToUtf8Json();
        GatewayDeploymentManifest parsed = GatewayDeploymentManifest.Parse(json);

        // Assert
        Encoding.UTF8.GetString(json).ShouldContain("\"gateway\":\"east\"", Case.Sensitive);
        Encoding.UTF8.GetString(json).ShouldContain("\"application\":{\"name\":\"root\"", Case.Sensitive);
        parsed.Gateway.Value.ShouldBe("east");
        parsed.Infrastructure!.Value.GetProperty("namespace").GetString().ShouldBe("east");
        parsed.ToUtf8Json().ShouldBe(json);
    }

    [Fact(DisplayName = DisplayPrefix + "The model of a child gateway keeps the whole hierarchy and deploys the child's part")]
    public void ToModel_ForChildGateway_ShouldSelectItsPart()
    {
        // Arrange
        var manifest = new GatewayDeploymentManifest("east", CreateManifest(), null, null);

        // Act
        ApplicationModel model = manifest.ToModel();

        // Assert
        model.Name.Value.ShouldBe("east");
        ((IApplicationModel)model).Role.ShouldBe(GatewayRole.Composite);
        model.Manifest.Name.Value.ShouldBe("root");
        model.Deployment.Resources.ShouldHaveSingleItem().Name.Value.ShouldBe("orders-api");
    }

    [Fact(DisplayName = DisplayPrefix + "Output values from the gateways above round-trip and reach the child's model")]
    public void ToModel_WithOutputs_ShouldCarryThemToTheModel()
    {
        // Arrange
        var identity = new ApplicationResourceOutput("root", "IdentityHub", "identity", OutputName.Endpoint);
        var value = new ApplicationResourceOutputValue(identity, "http://identity.root.svc");
        var manifest = new GatewayDeploymentManifest("east", CreateManifest(), [value], null);

        // Act
        byte[] json = manifest.ToUtf8Json();
        ApplicationModel model = GatewayDeploymentManifest.Parse(json).ToModel();

        // Assert
        Encoding.UTF8.GetString(json).ShouldContain(
            "\"outputs\":[{\"output\":{\"gateway\":\"root\",\"provider\":\"IdentityHub\",\"name\":\"identity\",\"output\":\"endpoint\"},\"value\":\"http://identity.root.svc\"}]",
            Case.Sensitive);
        model.Outputs.ShouldHaveSingleItem().ShouldBe(value);
    }

    [Fact(DisplayName = DisplayPrefix + "A resource's platform extensions travel as JSON values")]
    public void ToUtf8Json_WithExtension_ShouldCarryItsValueAsJson()
    {
        // Arrange
        using JsonDocument autoscaler = JsonDocument.Parse("""{"maxReplicas":5}""");
        ApplicationBuilder builder = Application.CreateBuilder("root", []);
        builder.AddGateway("east").AddResource("Web", "orders-api").WithExtension("kubernetes", "autoscaler", autoscaler.RootElement);
        var manifest = new GatewayDeploymentManifest("east", builder.Build().Model.Manifest, null, null);

        // Act
        byte[] json = manifest.ToUtf8Json();
        ApplicationModel model = GatewayDeploymentManifest.Parse(json).ToModel();

        // Assert
        Encoding.UTF8.GetString(json).ShouldContain(
            "\"extensions\":[{\"platform\":\"kubernetes\",\"name\":\"autoscaler\",\"value\":{\"maxReplicas\":5}}]",
            Case.Sensitive);
        model.Deployment.Resources.Single().Extensions.Single().Value.GetProperty("maxReplicas").GetInt32().ShouldBe(5);
    }

    [Fact(DisplayName = DisplayPrefix + "A manifest for a gateway the hierarchy does not declare is refused")]
    public void ToModel_ForUnknownGateway_ShouldThrow()
    {
        // Arrange
        var manifest = new GatewayDeploymentManifest("north", CreateManifest(), null, null);

        // Act & Assert
        Should.Throw<ArgumentException>(() => manifest.ToModel());
    }

    [Fact(DisplayName = DisplayPrefix + "The JSON literal null is not a deployment manifest")]
    public void Parse_WithJsonNull_ShouldThrowJsonException()
    {
        // Arrange, Act & Assert
        Should.Throw<JsonException>(() => GatewayDeploymentManifest.Parse("null"));
    }

    private static ApplicationManifest CreateManifest()
    {
        ApplicationBuilder builder = Application.CreateBuilder("root", []);
        IApplicationResourceDefinition identity = builder.AddResource("IdentityHub", "identity");
        builder.AddGateway("east").AddResource("Web", "orders-api").DependsOn(identity, "tokens.issue");
        return builder.Build().Model.Manifest;
    }
}

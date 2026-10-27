using System;
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
        var manifest = new GatewayDeploymentManifest("east", CreateManifest(), settings.RootElement.Clone());

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
        var manifest = new GatewayDeploymentManifest("east", CreateManifest(), null);

        // Act
        ApplicationModel model = manifest.ToModel();

        // Assert
        model.Name.Value.ShouldBe("east");
        ((IApplicationModel)model).Role.ShouldBe(GatewayRole.Composite);
        model.Manifest.Name.Value.ShouldBe("root");
        model.Deployment.Resources.ShouldHaveSingleItem().Name.Value.ShouldBe("orders-api");
    }

    [Fact(DisplayName = DisplayPrefix + "A manifest for a gateway the hierarchy does not declare is refused")]
    public void ToModel_ForUnknownGateway_ShouldThrow()
    {
        // Arrange
        var manifest = new GatewayDeploymentManifest("north", CreateManifest(), null);

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
        IApplicationResourceDescriptor identity = builder.AddResource("IdentityHub", "identity");
        builder.AddGateway("east").AddResource("Web", "orders-api").DependsOn(identity, "tokens.issue");
        return builder.Build().Model.Manifest;
    }
}

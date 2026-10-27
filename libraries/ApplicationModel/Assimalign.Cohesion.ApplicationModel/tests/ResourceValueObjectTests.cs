using System.IO;
using System.Text.Json;
using System.Text.Json.Nodes;

using Shouldly;
using Xunit;

namespace Assimalign.Cohesion.ApplicationModel.Tests;

public class ResourceValueObjectTests
{
    [Theory(DisplayName = "Cohesion Test [ApplicationModel] - TryCreate: Resource HTTP paths share one absolute path grammar")]
    [InlineData(null, false)]
    [InlineData("", false)]
    [InlineData("   ", false)]
    [InlineData("ready", false)]
    [InlineData("/ready?full=true", false)]
    [InlineData("/ready#state", false)]
    [InlineData("/", true)]
    [InlineData("/Ready%3Ffull%23state", true)]
    [InlineData("/health ready", true)]
    public void TryCreate_ResourceHttpPath_ShouldApplyAbsolutePathGrammar(string? text, bool expected)
    {
        // Act
        bool success = ResourceHttpPath.TryCreate(text, out ResourceHttpPath value);

        // Assert
        success.ShouldBe(expected);
        if (success)
        {
            value.Value.ShouldBe(text);
        }
    }

    [Theory(DisplayName = "Cohesion Test [ApplicationModel] - TryCreate: Mount sources share one case-sensitive expression grammar")]
    [InlineData(null, false)]
    [InlineData("", false)]
    [InlineData("parameter:", false)]
    [InlineData("parameter:   ", false)]
    [InlineData("source:   ", false)]
    [InlineData("   :key", false)]
    [InlineData("source:key:extra", false)]
    [InlineData("Literal:", false)]
    [InlineData("literal:", true)]
    [InlineData("literal:value:with:colons", true)]
    [InlineData("parameter: name ", true)]
    [InlineData("parameter:group:key", true)]
    [InlineData(" Source : Key ", true)]
    public void TryCreate_ResourceMountSource_ShouldPreserveExpressionGrammar(string? text, bool expected)
    {
        // Act
        bool success = ResourceMountSource.TryCreate(text, out ResourceMountSource value);

        // Assert
        success.ShouldBe(expected);
        if (success)
        {
            value.Value.ShouldBe(text);
        }
    }

    [Fact(DisplayName = "Cohesion Test [ApplicationModel] - Equality: Resource paths and source expressions compare ordinally")]
    public void Equals_CaseDifferences_ShouldRemainDistinct()
    {
        // Arrange
        ResourceHttpPath path = "/Ready";
        ResourceMountSource source = "Store:Key";

        // Act and Assert
        path.Equals((ResourceHttpPath)"/ready").ShouldBeFalse();
        source.Equals((ResourceMountSource)"store:key").ShouldBeFalse();
    }

    [Theory(DisplayName = "Cohesion Test [ApplicationModel] - Parse: Invalid manifest path and mount expressions fail typed deserialization")]
    [InlineData("http", "ready")]
    [InlineData("path", "cohesion/v1")]
    [InlineData("source", "parameter:   ")]
    public void Parse_InvalidManifestValue_ShouldRejectDuringDeserialization(string property, string text)
    {
        // Arrange
        var manifest = new ResourceManifest
        {
            Name = "app-api",
            Kind = "Web",
            Application = "app",
            ApplicationModel = "Assimalign.Cohesion.Web.ApplicationModel",
            Artifact = new ResourceManifestArtifact { Assembly = "Example.Api" },
            Endpoints = [new ResourceManifestEndpoint
            {
                Name = "http", Scheme = "http", Protocol = "tcp", ContainerPort = 8080
            }],
            Probes = new ResourceManifestProbes
            {
                Readiness = new ResourceManifestProbe { Endpoint = "http", Http = "/ready" }
            },
            ControlPlane = new ResourceManifestControlPlane { Endpoint = "http", Path = "/cohesion/v1" },
            Mounts = [new ResourceManifestMount
            {
                Name = "config", Kind = ResourceMountKind.Configuration,
                ContainerPath = "/config", Source = "parameter:config"
            }],
            Lifecycle = new ResourceManifestLifecycle { Workload = WorkloadKind.Deployment }
        };
        manifest.Validate();
        JsonNode document = JsonNode.Parse(JsonSerializer.Serialize(
            manifest, ResourceManifestJsonContext.Default.ResourceManifest)).ShouldNotBeNull();
        JsonNode target = property switch
        {
            "http" => document["probes"]!["readiness"]!,
            "path" => document["controlPlane"]!,
            _ => document["mounts"]![0]!
        };
        target[property] = text;
        string json = document.ToJsonString();

        // Act
        JsonException error = Should.Throw<JsonException>(() => ResourceManifest.Parse(json));

        // Assert
        error.Message.ShouldContain(property == "source" ? "A mount source must use" : "A resource HTTP path", Case.Sensitive);
    }

    [Fact(DisplayName = "Cohesion Test [ApplicationModel] - Conversion: Null optional paths and sources remain absent")]
    public void Convert_NullOptionalValues_ShouldPreserveOmission()
    {
        // Arrange
        string? text = null;

        // Act
        ResourceHttpPath? path = text;
        ResourceMountSource? source = text;

        // Assert
        path.ShouldBeNull();
        source.ShouldBeNull();
    }
}

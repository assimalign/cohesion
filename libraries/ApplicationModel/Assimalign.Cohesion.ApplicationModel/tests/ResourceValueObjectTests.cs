using System.Text.Json;

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

    [Theory(DisplayName = "Cohesion Test [ApplicationModel] - Parse: Invalid manifest path and mount expressions fail JSON conversion")]
    [InlineData("http", "ready")]
    [InlineData("path", "cohesion/v1")]
    [InlineData("source", "parameter:   ")]
    public void Parse_InvalidManifestValue_ShouldRejectJson(string property, string text)
    {
        // Arrange
        string json = property switch
        {
            "http" => "{\"probes\":{\"readiness\":{\"http\":\"" + text + "\"}}}",
            "path" => "{\"controlPlane\":{\"path\":\"" + text + "\"}}",
            _ => "{\"mounts\":[{\"source\":\"" + text + "\"}]}"
        };

        json = json.Insert(1, "\"schema\":\"cohesion/resource/v1\",");

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
        var probe = new ResourceManifestProbe { Http = text };
        var mount = new ResourceManifestMount { Source = text };

        // Assert
        probe.Http.ShouldBeNull();
        mount.Source.ShouldBeNull();
    }
}

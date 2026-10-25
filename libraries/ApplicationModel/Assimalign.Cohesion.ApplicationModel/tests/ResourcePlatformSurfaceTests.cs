using System;
using System.Collections.Generic;
using System.Text.Json;

using Shouldly;
using Xunit;

namespace Assimalign.Cohesion.ApplicationModel.Tests;

public sealed class ResourcePlatformSurfaceTests
{
    private const string Schema = """
        {"type":"object","properties":{"minimum":{"type":"integer"},"maximum":{"type":"integer"},"behavior":{"type":["object","null"],"properties":{"window":{"type":"integer"}}}},"required":["maximum"]}
        """;

    [Fact(DisplayName = "Cohesion Test [ApplicationModel] - Platform surface: Create keeps the key, schema and declared value")]
    public void Create_WithConformingValue_ShouldKeepItsParts()
    {
        // Act
        ResourcePlatformSurface surface = ResourcePlatformSurface.Create("kubernetes/hpa", Schema, Parse("""{"minimum":1,"maximum":3}"""));

        // Assert
        surface.Key.ShouldBe((ResourceExtensionKey)"kubernetes/hpa");
        surface.Schema.GetProperty("required")[0].GetString().ShouldBe("maximum");
        surface.Value.GetProperty("maximum").GetInt32().ShouldBe(3);
    }

    [Fact(DisplayName = "Cohesion Test [ApplicationModel] - Platform surface: Create refuses a declared value the schema rejects")]
    public void Create_WithNonconformingValue_ShouldThrow()
    {
        // Act
        ArgumentException exception = Should.Throw<ArgumentException>(
            () => ResourcePlatformSurface.Create("kubernetes/hpa", Schema, Parse("""{"minimum":1}""")));

        // Assert
        exception.Message.ShouldContain("kubernetes/hpa", Case.Sensitive);
    }

    [Fact(DisplayName = "Cohesion Test [ApplicationModel] - Platform surface: Create refuses a schema that is not a schema")]
    public void Create_WithArraySchema_ShouldThrow()
    {
        // Act
        Should.Throw<ArgumentException>(() => ResourcePlatformSurface.Create("kubernetes/hpa", "[]", Parse("{}")));
    }

    [Fact(DisplayName = "Cohesion Test [ApplicationModel] - Platform surface: an unknown member is refused, since a listed schema is closed")]
    public void TryValidatePatch_UnknownMember_ShouldRefuseWithItsPath()
    {
        // Arrange
        ResourcePlatformSurface surface = ResourcePlatformSurface.Create("kubernetes/hpa", Schema, Parse("""{"maximum":3}"""));

        // Act
        bool valid = surface.TryValidatePatch(Parse("""{"maximun":5}"""), out string? error);

        // Assert
        valid.ShouldBeFalse();
        error.ShouldNotBeNull();
        error.ShouldContain("maximun", Case.Sensitive);
    }

    [Fact(DisplayName = "Cohesion Test [ApplicationModel] - Platform surface: a patch may not remove a required member")]
    public void TryValidatePatch_RemovingRequiredMember_ShouldRefuse()
    {
        // Arrange
        ResourcePlatformSurface surface = ResourcePlatformSurface.Create("kubernetes/hpa", Schema, Parse("""{"maximum":3}"""));

        // Act
        bool valid = surface.TryValidatePatch(Parse("""{"maximum":null}"""), out _);

        // Assert
        valid.ShouldBeFalse();
    }

    [Fact(DisplayName = "Cohesion Test [ApplicationModel] - Platform surface: a platform rule refuses a value the schema cannot")]
    public void TryValidatePatch_RuleViolation_ShouldRefuseWithTheRulesReason()
    {
        // Arrange
        static string? Bounds(JsonElement value) =>
            value.TryGetProperty("minimum", out JsonElement minimum) && minimum.GetInt32() > value.GetProperty("maximum").GetInt32()
                ? "minimum exceeds maximum"
                : null;
        ResourcePlatformSurface surface = ResourcePlatformSurface.Create(
            "kubernetes/hpa", Schema, Parse("""{"minimum":1,"maximum":3}"""), Bounds);

        // Act
        bool valid = surface.TryValidatePatch(Parse("""{"minimum":9}"""), out string? error);

        // Assert
        valid.ShouldBeFalse();
        error.ShouldBe("minimum exceeds maximum");
        Should.Throw<ArgumentException>(() => ResourcePlatformSurface.Create(
            "kubernetes/hpa", Schema, Parse("""{"minimum":5,"maximum":3}"""), Bounds));
    }

    [Fact(DisplayName = "Cohesion Test [ApplicationModel] - Platform surface: Apply merges an override by RFC 7396, removing nulls and merging objects")]
    public void Apply_WithOverride_ShouldMergeByRfc7396()
    {
        // Arrange
        ResourcePlatformSurface surface = ResourcePlatformSurface.Create(
            "kubernetes/hpa", Schema, Parse("""{"minimum":1,"maximum":3,"behavior":{"window":30}}"""));

        // Act
        JsonElement value = surface.Apply(Parse("""{"minimum":null,"maximum":7,"behavior":{"window":60}}"""));
        JsonElement unchanged = surface.Apply(null);

        // Assert
        value.TryGetProperty("minimum", out _).ShouldBeFalse();
        value.GetProperty("maximum").GetInt32().ShouldBe(7);
        value.GetProperty("behavior").GetProperty("window").GetInt32().ShouldBe(60);
        unchanged.GetProperty("minimum").GetInt32().ShouldBe(1);
        surface.Value.GetProperty("maximum").GetInt32().ShouldBe(3);
    }

    [Fact(DisplayName = "Cohesion Test [ApplicationModel] - Inputs: a delivered record carries its directory, generation, platform overrides and restart digest")]
    public void WithDeploymentRecord_ShouldCarryTheRecordAndCopyTheOverrides()
    {
        // Arrange
        var overrides = new Dictionary<ResourceExtensionKey, JsonElement> { ["kubernetes/hpa"] = Parse("""{"maximum":5}""") };

        // Act
        ResourceInputs inputs = ResourceInputs.Empty.WithDeploymentRecord(
            "/var/run/cohesion/config", "0123456789abcdef", overrides, "sha256:0a1b");
        overrides.Clear();
        ResourceInputs withoutDigest = ResourceInputs.Empty.WithDeploymentRecord("/var/run/cohesion/config", "0123456789abcdef");

        // Assert
        inputs.ConfigurationDirectory.ShouldBe("/var/run/cohesion/config");
        inputs.DeploymentGeneration.ShouldBe("0123456789abcdef");
        inputs.PlatformOverrides[(ResourceExtensionKey)"kubernetes/hpa"].GetProperty("maximum").GetInt32().ShouldBe(5);
        inputs.RestartDigest.ShouldBe("sha256:0a1b");
        withoutDigest.RestartDigest.ShouldBeNull();
        withoutDigest.PlatformOverrides.ShouldBeEmpty();
        ResourceInputs.Empty.PlatformOverrides.ShouldBeEmpty();
        ResourceInputs.Empty.DeploymentGeneration.ShouldBeNull();
        ResourceInputs.Empty.RestartDigest.ShouldBeNull();
    }

    private static JsonElement Parse(string json)
    {
        using JsonDocument document = JsonDocument.Parse(json);
        return document.RootElement.Clone();
    }
}

using System.Linq;

using Shouldly;
using Xunit;

using Assimalign.Cohesion.OpenApi.Serialization;
using Assimalign.Cohesion.OpenApi.Validation;
using Assimalign.Cohesion.OpenApi.Versioning;

namespace Assimalign.Cohesion.OpenApi.Compliance.Tests;

/// <summary>
/// Drives corpus examples through the version transformer, turning the official upgrade guidance into
/// executable fixtures: an upgraded document must target the requested line and remain valid, and any
/// lossy step must be reported as a diagnostic.
/// </summary>
public class OpenApiUpgradeComplianceTests
{
    [Theory(DisplayName = "Cohesion Test [OpenApi.Compliance] - Upgrade: 3.0 examples upgrade to 3.1 and stay valid")]
    [InlineData("v3.0/petstore.json")]
    [InlineData("v3.0/callback-example.json")]
    [InlineData("v3.0/link-example.json")]
    [InlineData("v3.0/api-with-examples.json")]
    public void Upgrade_ThreeZeroToThreeOne_StaysValid(string relativePath)
    {
        var document = OpenApiJson.Parse(CorpusFixtures.ReadRelative(relativePath));
        document.SpecVersion.ShouldBe(OpenApiSpecVersion.V3_0);

        var result = document.TransformTo(OpenApiSpecVersion.V3_1);

        result.Document.SpecVersion.ShouldBe(OpenApiSpecVersion.V3_1);
        result.Document.Validate().Errors.ShouldBeEmpty();
        result.Document.ToJson(indented: false).ShouldContain("\"openapi\":\"3.1.2\"", Case.Sensitive);
    }

    [Theory(DisplayName = "Cohesion Test [OpenApi.Compliance] - Upgrade: 3.0 examples upgrade to 3.2 and stay valid")]
    [InlineData("v3.0/petstore.json")]
    [InlineData("v3.0/callback-example.json")]
    public void Upgrade_ThreeZeroToThreeTwo_StaysValid(string relativePath)
    {
        var document = OpenApiJson.Parse(CorpusFixtures.ReadRelative(relativePath));

        var result = document.TransformTo(OpenApiSpecVersion.V3_2);

        result.Document.SpecVersion.ShouldBe(OpenApiSpecVersion.V3_2);
        result.Document.Validate().Errors.ShouldBeEmpty();
    }

    [Theory(DisplayName = "Cohesion Test [OpenApi.Compliance] - Upgrade: 3.1 examples upgrade to 3.2 and stay valid")]
    [InlineData("v3.1/webhook-example.json")]
    [InlineData("v3.1/tictactoe.json")]
    [InlineData("v3.1/non-oauth-scopes.json")]
    public void Upgrade_ThreeOneToThreeTwo_StaysValid(string relativePath)
    {
        var document = OpenApiJson.Parse(CorpusFixtures.ReadRelative(relativePath));
        document.SpecVersion.ShouldBe(OpenApiSpecVersion.V3_1);

        var result = document.TransformTo(OpenApiSpecVersion.V3_2);

        result.Document.SpecVersion.ShouldBe(OpenApiSpecVersion.V3_2);
        result.Document.Validate().Errors.ShouldBeEmpty();
    }

    [Fact(DisplayName = "Cohesion Test [OpenApi.Compliance] - Downgrade: a 3.1 webhook document reports webhooks as unsupported in 3.0")]
    public void Downgrade_Webhooks_ReportsUnsupported()
    {
        var document = OpenApiJson.Parse(CorpusFixtures.ReadRelative("v3.1/webhook-example.json"));

        var result = document.TransformTo(OpenApiSpecVersion.V3_0);

        result.Diagnostics.ShouldContain(d =>
            d.Code == OpenApiTransformDiagnosticCodes.UnsupportedConstruct && d.Location.StartsWith("#/webhooks"));
    }

    [Fact(DisplayName = "Cohesion Test [OpenApi.Compliance] - Round-trip: an upgraded document is idempotent under a second identity transform")]
    public void Upgrade_ThenIdentity_IsStable()
    {
        var document = OpenApiJson.Parse(CorpusFixtures.ReadRelative("v3.0/petstore.json"));

        var upgraded = document.TransformTo(OpenApiSpecVersion.V3_1).Document;
        var identity = upgraded.TransformTo(OpenApiSpecVersion.V3_1);

        identity.Diagnostics.Where(d => d.Severity == OpenApiTransformSeverity.Warning).ShouldBeEmpty();
        identity.Document.ToJson(indented: false).ShouldBe(upgraded.ToJson(indented: false));
    }
}

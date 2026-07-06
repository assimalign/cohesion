using System;

using Shouldly;
using Xunit;

namespace Assimalign.Cohesion.OpenApi.Tests;

public class OpenApiVersionCapabilitiesTests
{
    [Theory(DisplayName = "Cohesion Test [OpenApi] - Capabilities: 3.1+ features gated off in 3.0")]
    [InlineData(OpenApiFeature.InfoSummary)]
    [InlineData(OpenApiFeature.LicenseIdentifier)]
    [InlineData(OpenApiFeature.Webhooks)]
    [InlineData(OpenApiFeature.JsonSchemaDialect)]
    [InlineData(OpenApiFeature.ComponentsPathItems)]
    [InlineData(OpenApiFeature.ReferenceSummaryAndDescription)]
    [InlineData(OpenApiFeature.MutualTlsSecurityScheme)]
    [InlineData(OpenApiFeature.SchemaTypeArray)]
    [InlineData(OpenApiFeature.SchemaNumericExclusiveBounds)]
    [InlineData(OpenApiFeature.SchemaConst)]
    [InlineData(OpenApiFeature.SchemaExamples)]
    [InlineData(OpenApiFeature.SchemaExtendedVocabulary)]
    [InlineData(OpenApiFeature.SchemaReferenceSiblingKeywords)]
    [InlineData(OpenApiFeature.SchemaBooleanForm)]
    public void Supports_ThreeOneFeatures_DisabledInThreeZero(OpenApiFeature feature)
    {
        OpenApiVersionCapabilities.Supports(feature, OpenApiSpecVersion.V3_0).ShouldBeFalse();
        OpenApiVersionCapabilities.Supports(feature, OpenApiSpecVersion.V3_1).ShouldBeTrue();
        OpenApiVersionCapabilities.Supports(feature, OpenApiSpecVersion.V3_2).ShouldBeTrue();
    }

    [Fact(DisplayName = "Cohesion Test [OpenApi] - Capabilities: nullable keyword only in 3.0")]
    public void Supports_NullableKeyword_OnlyThreeZero()
    {
        OpenApiVersionCapabilities.Supports(OpenApiFeature.SchemaNullableKeyword, OpenApiSpecVersion.V3_0).ShouldBeTrue();
        OpenApiVersionCapabilities.Supports(OpenApiFeature.SchemaNullableKeyword, OpenApiSpecVersion.V3_1).ShouldBeFalse();
        OpenApiVersionCapabilities.Supports(OpenApiFeature.SchemaNullableKeyword, OpenApiSpecVersion.V3_2).ShouldBeFalse();
    }

    [Theory(DisplayName = "Cohesion Test [OpenApi] - Capabilities: 3.2 features gated off below 3.2")]
    [InlineData(OpenApiFeature.PathItemAdditionalOperations)]
    [InlineData(OpenApiFeature.DocumentSelf)]
    [InlineData(OpenApiFeature.TagExtendedMetadata)]
    [InlineData(OpenApiFeature.OAuthDeviceAuthorizationFlow)]
    [InlineData(OpenApiFeature.ServerName)]
    [InlineData(OpenApiFeature.PathItemQueryOperation)]
    [InlineData(OpenApiFeature.ParameterQuerystringLocation)]
    [InlineData(OpenApiFeature.ParameterCookieStyle)]
    [InlineData(OpenApiFeature.MediaTypeStreamingFields)]
    [InlineData(OpenApiFeature.MediaTypeReference)]
    [InlineData(OpenApiFeature.ResponseSummary)]
    [InlineData(OpenApiFeature.ExampleDataAndSerializedValues)]
    [InlineData(OpenApiFeature.DiscriminatorDefaultMapping)]
    [InlineData(OpenApiFeature.XmlNodeType)]
    [InlineData(OpenApiFeature.SecuritySchemeOAuth2MetadataUrl)]
    [InlineData(OpenApiFeature.SecuritySchemeDeprecated)]
    [InlineData(OpenApiFeature.ComponentsMediaTypes)]
    [InlineData(OpenApiFeature.SecurityRequirementUriReference)]
    public void Supports_ThreeTwoFeatures_OnlyThreeTwo(OpenApiFeature feature)
    {
        OpenApiVersionCapabilities.Supports(feature, OpenApiSpecVersion.V3_0).ShouldBeFalse();
        OpenApiVersionCapabilities.Supports(feature, OpenApiSpecVersion.V3_1).ShouldBeFalse();
        OpenApiVersionCapabilities.Supports(feature, OpenApiSpecVersion.V3_2).ShouldBeTrue();
    }

    [Fact(DisplayName = "Cohesion Test [OpenApi] - Capabilities: every feature has a matrix entry for every line")]
    public void Supports_EveryFeature_HasMatrixEntry()
    {
        foreach (var feature in Enum.GetValues<OpenApiFeature>())
        {
            foreach (var version in Enum.GetValues<OpenApiSpecVersion>())
            {
                Should.NotThrow(() => OpenApiVersionCapabilities.Supports(feature, version));
            }
        }
    }

    [Theory(DisplayName = "Cohesion Test [OpenApi] - Capabilities: canonical version strings")]
    [InlineData(OpenApiSpecVersion.V3_0, "3.0.4")]
    [InlineData(OpenApiSpecVersion.V3_1, "3.1.2")]
    [InlineData(OpenApiSpecVersion.V3_2, "3.2.0")]
    public void GetVersionString_ReturnsCanonicalPatch(OpenApiSpecVersion version, string expected)
    {
        OpenApiVersionCapabilities.GetVersionString(version).ShouldBe(expected);
    }

    [Theory(DisplayName = "Cohesion Test [OpenApi] - Capabilities: parse openapi field to line")]
    [InlineData("3.0.0", OpenApiSpecVersion.V3_0)]
    [InlineData("3.0.4", OpenApiSpecVersion.V3_0)]
    [InlineData("3.1.0", OpenApiSpecVersion.V3_1)]
    [InlineData("3.2.0", OpenApiSpecVersion.V3_2)]
    public void TryParseVersion_KnownLines_MapsCorrectly(string field, OpenApiSpecVersion expected)
    {
        OpenApiVersionCapabilities.TryParseVersion(field, out var version).ShouldBeTrue();
        version.ShouldBe(expected);
    }

    [Theory(DisplayName = "Cohesion Test [OpenApi] - Capabilities: unknown openapi field rejected")]
    [InlineData("2.0")]
    [InlineData("4.0.0")]
    [InlineData("")]
    [InlineData(null)]
    public void TryParseVersion_UnknownLines_ReturnsFalse(string? field)
    {
        OpenApiVersionCapabilities.TryParseVersion(field, out _).ShouldBeFalse();
    }
}

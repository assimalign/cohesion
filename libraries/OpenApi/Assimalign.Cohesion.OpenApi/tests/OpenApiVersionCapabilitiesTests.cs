using FluentAssertions;
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
    public void Supports_ThreeOneFeatures_DisabledInThreeZero(OpenApiFeature feature)
    {
        OpenApiVersionCapabilities.Supports(feature, OpenApiSpecVersion.V3_0).Should().BeFalse();
        OpenApiVersionCapabilities.Supports(feature, OpenApiSpecVersion.V3_1).Should().BeTrue();
        OpenApiVersionCapabilities.Supports(feature, OpenApiSpecVersion.V3_2).Should().BeTrue();
    }

    [Fact(DisplayName = "Cohesion Test [OpenApi] - Capabilities: nullable keyword only in 3.0")]
    public void Supports_NullableKeyword_OnlyThreeZero()
    {
        OpenApiVersionCapabilities.Supports(OpenApiFeature.SchemaNullableKeyword, OpenApiSpecVersion.V3_0).Should().BeTrue();
        OpenApiVersionCapabilities.Supports(OpenApiFeature.SchemaNullableKeyword, OpenApiSpecVersion.V3_1).Should().BeFalse();
        OpenApiVersionCapabilities.Supports(OpenApiFeature.SchemaNullableKeyword, OpenApiSpecVersion.V3_2).Should().BeFalse();
    }

    [Theory(DisplayName = "Cohesion Test [OpenApi] - Capabilities: 3.2 features gated off below 3.2")]
    [InlineData(OpenApiFeature.PathItemAdditionalOperations)]
    [InlineData(OpenApiFeature.DocumentSelf)]
    [InlineData(OpenApiFeature.TagExtendedMetadata)]
    [InlineData(OpenApiFeature.OAuthDeviceAuthorizationFlow)]
    public void Supports_ThreeTwoFeatures_OnlyThreeTwo(OpenApiFeature feature)
    {
        OpenApiVersionCapabilities.Supports(feature, OpenApiSpecVersion.V3_0).Should().BeFalse();
        OpenApiVersionCapabilities.Supports(feature, OpenApiSpecVersion.V3_1).Should().BeFalse();
        OpenApiVersionCapabilities.Supports(feature, OpenApiSpecVersion.V3_2).Should().BeTrue();
    }

    [Theory(DisplayName = "Cohesion Test [OpenApi] - Capabilities: canonical version strings")]
    [InlineData(OpenApiSpecVersion.V3_0, "3.0.4")]
    [InlineData(OpenApiSpecVersion.V3_1, "3.1.2")]
    [InlineData(OpenApiSpecVersion.V3_2, "3.2.0")]
    public void GetVersionString_ReturnsCanonicalPatch(OpenApiSpecVersion version, string expected)
    {
        OpenApiVersionCapabilities.GetVersionString(version).Should().Be(expected);
    }

    [Theory(DisplayName = "Cohesion Test [OpenApi] - Capabilities: parse openapi field to line")]
    [InlineData("3.0.0", OpenApiSpecVersion.V3_0)]
    [InlineData("3.0.4", OpenApiSpecVersion.V3_0)]
    [InlineData("3.1.0", OpenApiSpecVersion.V3_1)]
    [InlineData("3.2.0", OpenApiSpecVersion.V3_2)]
    public void TryParseVersion_KnownLines_MapsCorrectly(string field, OpenApiSpecVersion expected)
    {
        OpenApiVersionCapabilities.TryParseVersion(field, out var version).Should().BeTrue();
        version.Should().Be(expected);
    }

    [Theory(DisplayName = "Cohesion Test [OpenApi] - Capabilities: unknown openapi field rejected")]
    [InlineData("2.0")]
    [InlineData("4.0.0")]
    [InlineData("")]
    [InlineData(null)]
    public void TryParseVersion_UnknownLines_ReturnsFalse(string? field)
    {
        OpenApiVersionCapabilities.TryParseVersion(field, out _).Should().BeFalse();
    }
}

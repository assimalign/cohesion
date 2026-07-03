using System;
using System.Diagnostics.CodeAnalysis;

namespace Assimalign.Cohesion.OpenApi;

/// <summary>
/// The version capability matrix for the supported OpenAPI lines. Declares which
/// <see cref="OpenApiFeature"/> values are valid for each <see cref="OpenApiSpecVersion"/> and maps
/// between the line and its canonical version string.
/// </summary>
/// <remarks>
/// This type is the single source of truth for version-gated behavior. Serialization consults it to
/// decide whether to emit a field for a target version; validation consults it to flag a field that
/// is not valid for the document's declared version. Keeping both consumers on one matrix prevents the
/// two from drifting apart.
/// </remarks>
public static class OpenApiVersionCapabilities
{
    /// <summary>
    /// Determines whether <paramref name="feature"/> is supported by <paramref name="version"/>.
    /// </summary>
    /// <param name="feature">The version-gated feature to test.</param>
    /// <param name="version">The OpenAPI line to test against.</param>
    /// <returns><see langword="true"/> when the feature is valid for the version; otherwise <see langword="false"/>.</returns>
    public static bool Supports(OpenApiFeature feature, OpenApiSpecVersion version) => feature switch
    {
        // 3.1+ additions.
        OpenApiFeature.InfoSummary
            or OpenApiFeature.LicenseIdentifier
            or OpenApiFeature.Webhooks
            or OpenApiFeature.JsonSchemaDialect
            or OpenApiFeature.ComponentsPathItems
            or OpenApiFeature.ReferenceSummaryAndDescription
            or OpenApiFeature.MutualTlsSecurityScheme
            or OpenApiFeature.SchemaTypeArray
            or OpenApiFeature.SchemaNumericExclusiveBounds
            or OpenApiFeature.SchemaConst
            or OpenApiFeature.SchemaExamples
            or OpenApiFeature.SchemaExtendedVocabulary
            or OpenApiFeature.SchemaReferenceSiblingKeywords
            or OpenApiFeature.SchemaBooleanForm => version is OpenApiSpecVersion.V3_1 or OpenApiSpecVersion.V3_2,

        // 3.0-only.
        OpenApiFeature.SchemaNullableKeyword => version is OpenApiSpecVersion.V3_0,

        // 3.2+ additions.
        OpenApiFeature.PathItemAdditionalOperations
            or OpenApiFeature.DocumentSelf
            or OpenApiFeature.TagExtendedMetadata
            or OpenApiFeature.OAuthDeviceAuthorizationFlow
            or OpenApiFeature.ServerName
            or OpenApiFeature.PathItemQueryOperation
            or OpenApiFeature.ParameterQuerystringLocation
            or OpenApiFeature.ParameterCookieStyle
            or OpenApiFeature.MediaTypeStreamingFields
            or OpenApiFeature.MediaTypeReference
            or OpenApiFeature.ResponseSummary
            or OpenApiFeature.ExampleDataAndSerializedValues
            or OpenApiFeature.DiscriminatorDefaultMapping
            or OpenApiFeature.XmlNodeType
            or OpenApiFeature.SecuritySchemeOAuth2MetadataUrl
            or OpenApiFeature.SecuritySchemeDeprecated
            or OpenApiFeature.ComponentsMediaTypes
            or OpenApiFeature.SecurityRequirementUriReference => version is OpenApiSpecVersion.V3_2,

        _ => throw new ArgumentOutOfRangeException(nameof(feature), feature, "Unknown OpenAPI feature.")
    };

    /// <summary>
    /// Gets the canonical version string emitted for an OpenAPI line.
    /// </summary>
    /// <param name="version">The OpenAPI line.</param>
    /// <returns><c>3.0.4</c>, <c>3.1.2</c>, or <c>3.2.0</c>.</returns>
    /// <exception cref="ArgumentOutOfRangeException">Thrown when <paramref name="version"/> is not a known line.</exception>
    public static string GetVersionString(OpenApiSpecVersion version) => version switch
    {
        OpenApiSpecVersion.V3_0 => "3.0.4",
        OpenApiSpecVersion.V3_1 => "3.1.2",
        OpenApiSpecVersion.V3_2 => "3.2.0",
        _ => throw new ArgumentOutOfRangeException(nameof(version), version, "Unknown OpenAPI specification version.")
    };

    /// <summary>
    /// Maps an <c>openapi</c> field value to its line. Any <c>3.0.x</c> maps to <see cref="OpenApiSpecVersion.V3_0"/>,
    /// any <c>3.1.x</c> to <see cref="OpenApiSpecVersion.V3_1"/>, and any <c>3.2.x</c> to <see cref="OpenApiSpecVersion.V3_2"/>.
    /// </summary>
    /// <param name="openApiField">The raw <c>openapi</c> field value, for example <c>3.1.0</c>.</param>
    /// <param name="version">When this method returns, the resolved line when recognized.</param>
    /// <returns><see langword="true"/> when the value maps to a supported line; otherwise <see langword="false"/>.</returns>
    public static bool TryParseVersion(string? openApiField, out OpenApiSpecVersion version)
    {
        version = default;
        if (string.IsNullOrWhiteSpace(openApiField))
        {
            return false;
        }

        if (openApiField.StartsWith("3.0", StringComparison.Ordinal))
        {
            version = OpenApiSpecVersion.V3_0;
            return true;
        }

        if (openApiField.StartsWith("3.1", StringComparison.Ordinal))
        {
            version = OpenApiSpecVersion.V3_1;
            return true;
        }

        if (openApiField.StartsWith("3.2", StringComparison.Ordinal))
        {
            version = OpenApiSpecVersion.V3_2;
            return true;
        }

        return false;
    }
}

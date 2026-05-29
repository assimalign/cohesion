namespace Assimalign.Cohesion.OpenApi;

/// <summary>
/// Enumerates OpenAPI description features whose availability or semantics differ between the
/// supported specification lines. <see cref="OpenApiVersionCapabilities"/> maps each feature to the
/// set of versions that support it, giving serialization and validation a single source of truth for
/// version-gated behavior.
/// </summary>
public enum OpenApiFeature
{
    /// <summary>The <c>summary</c> field on the Info Object (3.1+).</summary>
    InfoSummary,

    /// <summary>The SPDX <c>identifier</c> field on the License Object (3.1+).</summary>
    LicenseIdentifier,

    /// <summary>The top-level <c>webhooks</c> map (3.1+).</summary>
    Webhooks,

    /// <summary>The top-level <c>jsonSchemaDialect</c> field (3.1+).</summary>
    JsonSchemaDialect,

    /// <summary>The <c>pathItems</c> map inside the Components Object (3.1+).</summary>
    ComponentsPathItems,

    /// <summary>The <c>summary</c> and <c>description</c> sibling fields alongside <c>$ref</c> on a Reference Object (3.1+).</summary>
    ReferenceSummaryAndDescription,

    /// <summary>The <c>mutualTLS</c> security scheme type (3.1+).</summary>
    MutualTlsSecurityScheme,

    /// <summary>The 3.0-only <c>nullable</c> schema keyword. In 3.1+ nullability is expressed by adding <c>"null"</c> to the schema <c>type</c>.</summary>
    SchemaNullableKeyword,

    /// <summary>A schema <c>type</c> expressed as an array of types, including <c>"null"</c> (3.1+).</summary>
    SchemaTypeArray,

    /// <summary>Numeric <c>exclusiveMinimum</c>/<c>exclusiveMaximum</c> (3.1+). In 3.0 these are booleans paired with <c>minimum</c>/<c>maximum</c>.</summary>
    SchemaNumericExclusiveBounds,

    /// <summary>The JSON Schema <c>const</c> keyword (3.1+).</summary>
    SchemaConst,

    /// <summary>The schema-level <c>examples</c> array (3.1+). In 3.0 only the singular <c>example</c> is available.</summary>
    SchemaExamples,

    /// <summary>The <c>additionalOperations</c> map on a Path Item Object (3.2+).</summary>
    PathItemAdditionalOperations,

    /// <summary>The top-level <c>$self</c> field (3.2+).</summary>
    DocumentSelf,

    /// <summary>The extended Tag Object metadata fields <c>summary</c>, <c>parent</c>, and <c>kind</c> (3.2+).</summary>
    TagExtendedMetadata,

    /// <summary>The OAuth2 <c>deviceAuthorization</c> flow (3.2+).</summary>
    OAuthDeviceAuthorizationFlow
}

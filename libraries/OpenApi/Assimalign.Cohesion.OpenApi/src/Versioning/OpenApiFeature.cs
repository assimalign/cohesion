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
    OAuthDeviceAuthorizationFlow,

    /// <summary>The <c>name</c> field on the Server Object (3.2+).</summary>
    ServerName,

    /// <summary>The <c>query</c> fixed operation field on a Path Item Object (3.2+).</summary>
    PathItemQueryOperation,

    /// <summary>The <c>querystring</c> parameter location (3.2+).</summary>
    ParameterQuerystringLocation,

    /// <summary>The <c>cookie</c> parameter style (3.2+).</summary>
    ParameterCookieStyle,

    /// <summary>
    /// The sequential and multipart streaming fields (3.2+): <c>itemSchema</c>, <c>prefixEncoding</c>, and
    /// <c>itemEncoding</c> on the Media Type Object, and the nested <c>encoding</c>, <c>prefixEncoding</c>,
    /// and <c>itemEncoding</c> fields on the Encoding Object.
    /// </summary>
    MediaTypeStreamingFields,

    /// <summary>A Reference Object used as a <c>content</c> map value in place of a Media Type Object (3.2+).</summary>
    MediaTypeReference,

    /// <summary>The <c>summary</c> field on the Response Object (3.2+).</summary>
    ResponseSummary,

    /// <summary>The <c>dataValue</c> and <c>serializedValue</c> fields on the Example Object (3.2+).</summary>
    ExampleDataAndSerializedValues,

    /// <summary>The <c>defaultMapping</c> field on the Discriminator Object (3.2+).</summary>
    DiscriminatorDefaultMapping,

    /// <summary>The <c>nodeType</c> field on the XML Object (3.2+).</summary>
    XmlNodeType,

    /// <summary>The <c>oauth2MetadataUrl</c> field on the Security Scheme Object (3.2+).</summary>
    SecuritySchemeOAuth2MetadataUrl,

    /// <summary>The <c>deprecated</c> field on the Security Scheme Object (3.2+).</summary>
    SecuritySchemeDeprecated,

    /// <summary>The <c>mediaTypes</c> map inside the Components Object (3.2+).</summary>
    ComponentsMediaTypes,

    /// <summary>A Security Requirement Object name given as the URI of a Security Scheme Object rather than a component name (3.2+).</summary>
    SecurityRequirementUriReference,

    /// <summary>
    /// The JSON Schema draft 2020-12 vocabulary available to Schema Objects from 3.1 onward:
    /// <c>$defs</c>, <c>$id</c>, <c>$anchor</c>, <c>$dynamicRef</c>, <c>$dynamicAnchor</c>, <c>$comment</c>,
    /// <c>$schema</c>, <c>if</c>/<c>then</c>/<c>else</c>, <c>dependentSchemas</c>, <c>dependentRequired</c>,
    /// <c>prefixItems</c>, <c>contains</c>/<c>minContains</c>/<c>maxContains</c>, <c>patternProperties</c>,
    /// <c>propertyNames</c>, <c>unevaluatedItems</c>/<c>unevaluatedProperties</c>, and
    /// <c>contentEncoding</c>/<c>contentMediaType</c>/<c>contentSchema</c>.
    /// </summary>
    SchemaExtendedVocabulary,

    /// <summary>Keywords alongside <c>$ref</c> within a Schema Object (3.1+). In 3.0 any siblings of <c>$ref</c> are ignored.</summary>
    SchemaReferenceSiblingKeywords,

    /// <summary>The boolean schema forms <c>true</c> and <c>false</c> (3.1+).</summary>
    SchemaBooleanForm
}

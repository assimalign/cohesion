using System.Collections.Generic;

namespace Assimalign.Cohesion.OpenApi;

/// <summary>
/// A schema for input and output data types. See the "Schema Object" section of the OpenAPI Specification.
/// </summary>
/// <remarks>
/// <para>
/// The model carries the union of the JSON Schema vocabulary used across the supported lines. Two version
/// differences are normalized here so callers author once and the serializer adapts per target version:
/// </para>
/// <list type="bullet">
/// <item>
/// <description>
/// <b>Nullability.</b> Set <see cref="Type"/> plus <see cref="Nullable"/>. For 3.0 the writer emits the
/// <c>nullable</c> keyword; for 3.1+ it emits a type array such as <c>["string", "null"]</c>.
/// </description>
/// </item>
/// <item>
/// <description>
/// <b>Exclusive bounds.</b> <see cref="ExclusiveMinimum"/>/<see cref="ExclusiveMaximum"/> are numeric. For
/// 3.0 the writer emits the paired <c>minimum</c>/<c>maximum</c> value with a boolean exclusive flag; for
/// 3.1+ it emits the numeric form directly.
/// </description>
/// </item>
/// </list>
/// <para>
/// From 3.1 onward a Schema Object is a full JSON Schema draft 2020-12 schema: <c>type</c> may carry
/// multiple entries (<see cref="Types"/>), a schema may be the boolean form <c>true</c>/<c>false</c>
/// (<see cref="BooleanValue"/>), and keywords may appear alongside <see cref="Reference"/>. Those surfaces
/// are version-gated for 3.0 targets by <see cref="OpenApiVersionCapabilities"/>.
/// </para>
/// </remarks>
public sealed class OpenApiSchema : IOpenApiReferenceable, IOpenApiExtensible
{
    /// <inheritdoc/>
    /// <remarks>From OpenAPI 3.1 onward other keywords may be set alongside the reference; in 3.0 they are invalid next to <c>$ref</c>.</remarks>
    public OpenApiReference? Reference { get; set; }

    /// <summary>
    /// Gets or sets the boolean schema form (OpenAPI 3.1+). When set, the schema serializes as the JSON
    /// Schema literal <c>true</c> or <c>false</c> and every other member is ignored.
    /// </summary>
    public bool? BooleanValue { get; set; }

    /// <summary>Gets or sets the schema resource identity URI (the <c>$id</c> keyword, OpenAPI 3.1+).</summary>
    public string? Id { get; set; }

    /// <summary>Gets or sets the dialect of this schema resource (the <c>$schema</c> keyword, OpenAPI 3.1+).</summary>
    public string? Dialect { get; set; }

    /// <summary>Gets or sets the plain-name fragment anchor (the <c>$anchor</c> keyword, OpenAPI 3.1+).</summary>
    public string? Anchor { get; set; }

    /// <summary>Gets or sets the dynamic reference (the <c>$dynamicRef</c> keyword, OpenAPI 3.1+).</summary>
    public string? DynamicRef { get; set; }

    /// <summary>Gets or sets the dynamic anchor (the <c>$dynamicAnchor</c> keyword, OpenAPI 3.1+).</summary>
    public string? DynamicAnchor { get; set; }

    /// <summary>Gets or sets a comment for schema maintainers (the <c>$comment</c> keyword, OpenAPI 3.1+).</summary>
    public string? Comment { get; set; }

    /// <summary>Gets the reusable schema definitions embedded in this schema resource (the <c>$defs</c> keyword, OpenAPI 3.1+), keyed by definition name.</summary>
    public IDictionary<string, OpenApiSchema> Defs { get; } = new Dictionary<string, OpenApiSchema>();

    /// <summary>Gets or sets the title of the schema.</summary>
    public string? Title { get; set; }

    /// <summary>Gets or sets a description of the schema. CommonMark syntax may be used.</summary>
    public string? Description { get; set; }

    /// <summary>
    /// Gets the instance types the schema describes. A single entry is emitted as a string; multiple
    /// entries require OpenAPI 3.1+ and are emitted as a type array. Nullability is expressed through
    /// <see cref="Nullable"/> rather than a <see cref="SchemaType.Null"/> entry.
    /// </summary>
    public IList<SchemaType> Types { get; } = new List<SchemaType>();

    /// <summary>
    /// Gets or sets the primary instance type the schema describes. Reading returns the first entry of
    /// <see cref="Types"/>; assigning replaces the whole list. Use <see cref="Types"/> directly for
    /// multi-type schemas (OpenAPI 3.1+).
    /// </summary>
    public SchemaType? Type
    {
        get => Types.Count > 0 ? Types[0] : null;
        set
        {
            Types.Clear();
            if (value.HasValue)
            {
                Types.Add(value.Value);
            }
        }
    }

    /// <summary>Gets or sets a value indicating whether the value may be null. See the remarks on <see cref="OpenApiSchema"/> for the version-specific emission.</summary>
    public bool Nullable { get; set; }

    /// <summary>Gets or sets the format modifier for the <see cref="Type"/>, for example <c>date-time</c> or <c>int64</c>.</summary>
    public string? Format { get; set; }

    /// <summary>Gets or sets the default value for the schema.</summary>
    public OpenApiNode? Default { get; set; }

    /// <summary>Gets or sets a single example for the schema. Available in all supported versions.</summary>
    public OpenApiNode? Example { get; set; }

    /// <summary>Gets the example values for the schema (OpenAPI 3.1+).</summary>
    public IList<OpenApiNode> Examples { get; } = new List<OpenApiNode>();

    /// <summary>Gets the enumeration of valid values for the schema.</summary>
    public IList<OpenApiNode> Enum { get; } = new List<OpenApiNode>();

    /// <summary>Gets or sets the single valid constant value for the schema (OpenAPI 3.1+).</summary>
    public OpenApiNode? Const { get; set; }

    /// <summary>Gets or sets the value the instance must be a multiple of.</summary>
    public double? MultipleOf { get; set; }

    /// <summary>Gets or sets the inclusive upper bound for a numeric instance.</summary>
    public double? Maximum { get; set; }

    /// <summary>Gets or sets the exclusive upper bound for a numeric instance. See the remarks on <see cref="OpenApiSchema"/> for version-specific emission.</summary>
    public double? ExclusiveMaximum { get; set; }

    /// <summary>Gets or sets the inclusive lower bound for a numeric instance.</summary>
    public double? Minimum { get; set; }

    /// <summary>Gets or sets the exclusive lower bound for a numeric instance. See the remarks on <see cref="OpenApiSchema"/> for version-specific emission.</summary>
    public double? ExclusiveMinimum { get; set; }

    /// <summary>Gets or sets the maximum length of a string instance.</summary>
    public int? MaxLength { get; set; }

    /// <summary>Gets or sets the minimum length of a string instance.</summary>
    public int? MinLength { get; set; }

    /// <summary>Gets or sets a regular expression that a string instance must match.</summary>
    public string? Pattern { get; set; }

    /// <summary>Gets or sets the maximum number of items in an array instance.</summary>
    public int? MaxItems { get; set; }

    /// <summary>Gets or sets the minimum number of items in an array instance.</summary>
    public int? MinItems { get; set; }

    /// <summary>Gets or sets a value indicating whether array items must be unique.</summary>
    public bool? UniqueItems { get; set; }

    /// <summary>Gets or sets the maximum number of properties on an object instance.</summary>
    public int? MaxProperties { get; set; }

    /// <summary>Gets or sets the minimum number of properties on an object instance.</summary>
    public int? MinProperties { get; set; }

    /// <summary>Gets the names of required properties for an object instance.</summary>
    public IList<string> Required { get; } = new List<string>();

    /// <summary>Gets the schemas describing object properties, keyed by property name.</summary>
    public IDictionary<string, OpenApiSchema> Properties { get; } = new Dictionary<string, OpenApiSchema>();

    /// <summary>Gets or sets the schema for additional properties. Mutually exclusive with <see cref="AdditionalPropertiesAllowed"/>.</summary>
    public OpenApiSchema? AdditionalProperties { get; set; }

    /// <summary>Gets or sets a value indicating whether additional properties are allowed, when expressed as a boolean rather than a schema.</summary>
    public bool? AdditionalPropertiesAllowed { get; set; }

    /// <summary>Gets the schemas for properties whose names match a regular expression, keyed by pattern (the <c>patternProperties</c> keyword, OpenAPI 3.1+).</summary>
    public IDictionary<string, OpenApiSchema> PatternProperties { get; } = new Dictionary<string, OpenApiSchema>();

    /// <summary>Gets or sets the schema that every property name of an object instance must validate against (the <c>propertyNames</c> keyword, OpenAPI 3.1+).</summary>
    public OpenApiSchema? PropertyNames { get; set; }

    /// <summary>Gets or sets the schema applied to object properties not evaluated by other keywords (the <c>unevaluatedProperties</c> keyword, OpenAPI 3.1+).</summary>
    public OpenApiSchema? UnevaluatedProperties { get; set; }

    /// <summary>Gets the property names that become required when a given property is present, keyed by trigger property (the <c>dependentRequired</c> keyword, OpenAPI 3.1+).</summary>
    public IDictionary<string, IList<string>> DependentRequired { get; } = new Dictionary<string, IList<string>>();

    /// <summary>Gets the schemas applied when a given property is present, keyed by trigger property (the <c>dependentSchemas</c> keyword, OpenAPI 3.1+).</summary>
    public IDictionary<string, OpenApiSchema> DependentSchemas { get; } = new Dictionary<string, OpenApiSchema>();

    /// <summary>Gets or sets the schema describing array items.</summary>
    public OpenApiSchema? Items { get; set; }

    /// <summary>Gets the positional schemas for the leading items of a tuple-style array (the <c>prefixItems</c> keyword, OpenAPI 3.1+).</summary>
    public IList<OpenApiSchema> PrefixItems { get; } = new List<OpenApiSchema>();

    /// <summary>Gets or sets the schema that at least one array item must validate against (the <c>contains</c> keyword, OpenAPI 3.1+).</summary>
    public OpenApiSchema? Contains { get; set; }

    /// <summary>Gets or sets the minimum number of array items that must validate against <see cref="Contains"/> (OpenAPI 3.1+).</summary>
    public int? MinContains { get; set; }

    /// <summary>Gets or sets the maximum number of array items that may validate against <see cref="Contains"/> (OpenAPI 3.1+).</summary>
    public int? MaxContains { get; set; }

    /// <summary>Gets or sets the schema applied to array items not evaluated by other keywords (the <c>unevaluatedItems</c> keyword, OpenAPI 3.1+).</summary>
    public OpenApiSchema? UnevaluatedItems { get; set; }

    /// <summary>Gets or sets the schema applied when <see cref="If"/> validates successfully (the <c>then</c> keyword, OpenAPI 3.1+).</summary>
    public OpenApiSchema? Then { get; set; }

    /// <summary>Gets or sets the conditional schema (the <c>if</c> keyword, OpenAPI 3.1+).</summary>
    public OpenApiSchema? If { get; set; }

    /// <summary>Gets or sets the schema applied when <see cref="If"/> fails validation (the <c>else</c> keyword, OpenAPI 3.1+).</summary>
    public OpenApiSchema? Else { get; set; }

    /// <summary>Gets or sets the encoding of a string instance's content, for example <c>base64</c> (the <c>contentEncoding</c> keyword, OpenAPI 3.1+).</summary>
    public string? ContentEncoding { get; set; }

    /// <summary>Gets or sets the media type of a string instance's content (the <c>contentMediaType</c> keyword, OpenAPI 3.1+).</summary>
    public string? ContentMediaType { get; set; }

    /// <summary>Gets or sets the schema describing a string instance's decoded content (the <c>contentSchema</c> keyword, OpenAPI 3.1+).</summary>
    public OpenApiSchema? ContentSchema { get; set; }

    /// <summary>Gets the schemas this schema must all validate against.</summary>
    public IList<OpenApiSchema> AllOf { get; } = new List<OpenApiSchema>();

    /// <summary>Gets the schemas this schema must validate against at least one of.</summary>
    public IList<OpenApiSchema> AnyOf { get; } = new List<OpenApiSchema>();

    /// <summary>Gets the schemas this schema must validate against exactly one of.</summary>
    public IList<OpenApiSchema> OneOf { get; } = new List<OpenApiSchema>();

    /// <summary>Gets or sets a schema this schema must not validate against.</summary>
    public OpenApiSchema? Not { get; set; }

    /// <summary>Gets or sets the discriminator used to aid in serialization, deserialization, and validation of polymorphic schemas.</summary>
    public OpenApiDiscriminator? Discriminator { get; set; }

    /// <summary>Gets or sets a value indicating whether the property is read-only.</summary>
    public bool ReadOnly { get; set; }

    /// <summary>Gets or sets a value indicating whether the property is write-only.</summary>
    public bool WriteOnly { get; set; }

    /// <summary>Gets or sets additional metadata describing the XML representation of this schema.</summary>
    public OpenApiXml? Xml { get; set; }

    /// <summary>Gets or sets additional external documentation for this schema.</summary>
    public OpenApiExternalDocumentation? ExternalDocs { get; set; }

    /// <summary>Gets or sets a value indicating whether the schema is deprecated.</summary>
    public bool Deprecated { get; set; }

    /// <inheritdoc/>
    public IDictionary<string, OpenApiNode> Extensions { get; } = new Dictionary<string, OpenApiNode>();
}

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
/// </remarks>
public sealed class OpenApiSchema : IOpenApiReferenceable, IOpenApiExtensible
{
    /// <inheritdoc/>
    public OpenApiReference? Reference { get; set; }

    /// <summary>Gets or sets the title of the schema.</summary>
    public string? Title { get; set; }

    /// <summary>Gets or sets a description of the schema. CommonMark syntax may be used.</summary>
    public string? Description { get; set; }

    /// <summary>Gets or sets the instance type the schema describes.</summary>
    public SchemaType? Type { get; set; }

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

    /// <summary>Gets or sets the schema describing array items.</summary>
    public OpenApiSchema? Items { get; set; }

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

namespace Assimalign.Cohesion.OpenApi.Attributes;

/// <summary>
/// The flat intermediate metadata for a schema property, produced from an
/// <see cref="OpenApiSchemaPropertyAttribute"/>.
/// </summary>
public sealed class OpenApiSchemaPropertyMetadata
{
    /// <summary>Gets the property name.</summary>
    public required string Name { get; init; }

    /// <summary>Gets the property description.</summary>
    public string? Description { get; init; }

    /// <summary>Gets a value indicating whether the property is required.</summary>
    public bool Required { get; init; }

    /// <summary>Gets a value indicating whether the property value may be null.</summary>
    public bool Nullable { get; init; }

    /// <summary>Gets the scalar schema type of the property, if declared.</summary>
    public SchemaType? SchemaType { get; init; }

    /// <summary>Gets the format modifier, if any.</summary>
    public string? Format { get; init; }

    /// <summary>Gets a schema reference for a complex property type, if any.</summary>
    public string? SchemaReference { get; init; }
}

namespace Assimalign.Cohesion.OpenApi.Attributes;

/// <summary>
/// The flat intermediate metadata for an operation parameter, produced from an
/// <see cref="OpenApiParameterAttribute"/>. This is the neutral representation the source generator
/// emits and the generation pipeline consumes.
/// </summary>
public sealed class OpenApiParameterMetadata
{
    /// <summary>Gets the parameter name.</summary>
    public required string Name { get; init; }

    /// <summary>Gets the parameter location.</summary>
    public required ParameterLocation In { get; init; }

    /// <summary>Gets the parameter description.</summary>
    public string? Description { get; init; }

    /// <summary>Gets a value indicating whether the parameter is required.</summary>
    public bool Required { get; init; }

    /// <summary>Gets a value indicating whether the parameter is deprecated.</summary>
    public bool Deprecated { get; init; }

    /// <summary>Gets the scalar schema type of the parameter, if one was declared.</summary>
    public SchemaType? SchemaType { get; init; }

    /// <summary>Gets the format modifier of the parameter schema, if any.</summary>
    public string? Format { get; init; }
}

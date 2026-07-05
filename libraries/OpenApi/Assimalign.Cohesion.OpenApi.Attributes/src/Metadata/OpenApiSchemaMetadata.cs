using System.Collections.Generic;

namespace Assimalign.Cohesion.OpenApi.Attributes;

/// <summary>
/// The flat intermediate metadata for a schema component, produced from an
/// <see cref="OpenApiSchemaAttribute"/> and its member <see cref="OpenApiSchemaPropertyAttribute"/>s.
/// </summary>
public sealed class OpenApiSchemaMetadata
{
    /// <summary>Gets the component name.</summary>
    public required string Name { get; init; }

    /// <summary>Gets the schema title.</summary>
    public string? Title { get; init; }

    /// <summary>Gets the schema description.</summary>
    public string? Description { get; init; }

    /// <summary>Gets the instance type of the schema.</summary>
    public SchemaType Type { get; init; } = SchemaType.Object;

    /// <summary>Gets a value indicating whether the schema is deprecated.</summary>
    public bool Deprecated { get; init; }

    /// <summary>Gets the schema properties.</summary>
    public IReadOnlyList<OpenApiSchemaPropertyMetadata> Properties { get; init; } = [];
}

namespace Assimalign.Cohesion.OpenApi.Attributes;

/// <summary>
/// The flat intermediate metadata for a document tag, produced from an <see cref="OpenApiTagAttribute"/>.
/// </summary>
public sealed class OpenApiTagMetadata
{
    /// <summary>Gets the tag name.</summary>
    public required string Name { get; init; }

    /// <summary>Gets the tag description.</summary>
    public string? Description { get; init; }

    /// <summary>Gets a short summary of the tag (OpenAPI 3.2+).</summary>
    public string? Summary { get; init; }

    /// <summary>Gets the parent tag name (OpenAPI 3.2+).</summary>
    public string? Parent { get; init; }

    /// <summary>Gets the machine-readable tag kind (OpenAPI 3.2+).</summary>
    public string? Kind { get; init; }
}

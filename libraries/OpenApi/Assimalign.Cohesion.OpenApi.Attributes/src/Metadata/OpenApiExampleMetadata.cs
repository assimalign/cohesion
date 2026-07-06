namespace Assimalign.Cohesion.OpenApi.Attributes;

/// <summary>
/// The flat intermediate metadata for a named example, produced from an
/// <see cref="OpenApiExampleAttribute"/>.
/// </summary>
public sealed class OpenApiExampleMetadata
{
    /// <summary>Gets the example name.</summary>
    public required string Name { get; init; }

    /// <summary>Gets a short summary of the example.</summary>
    public string? Summary { get; init; }

    /// <summary>Gets a long description of the example.</summary>
    public string? Description { get; init; }

    /// <summary>Gets the example value as a serialized string, if embedded.</summary>
    public string? Value { get; init; }

    /// <summary>Gets a URI identifying an external example, if used.</summary>
    public string? ExternalValue { get; init; }
}

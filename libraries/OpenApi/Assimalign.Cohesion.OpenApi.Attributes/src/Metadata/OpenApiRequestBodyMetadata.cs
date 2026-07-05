namespace Assimalign.Cohesion.OpenApi.Attributes;

/// <summary>
/// The flat intermediate metadata for an operation request body, produced from an
/// <see cref="OpenApiRequestBodyAttribute"/>.
/// </summary>
public sealed class OpenApiRequestBodyMetadata
{
    /// <summary>Gets the media type of the request body.</summary>
    public required string ContentType { get; init; }

    /// <summary>Gets the request body description.</summary>
    public string? Description { get; init; }

    /// <summary>Gets a value indicating whether the request body is required.</summary>
    public bool Required { get; init; }

    /// <summary>Gets the resolved schema reference for the body, if any.</summary>
    public string? SchemaReference { get; init; }
}

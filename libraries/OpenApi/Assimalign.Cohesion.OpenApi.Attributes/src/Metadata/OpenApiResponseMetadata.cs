namespace Assimalign.Cohesion.OpenApi.Attributes;

/// <summary>
/// The flat intermediate metadata for one operation response, produced from an
/// <see cref="OpenApiResponseAttribute"/>.
/// </summary>
public sealed class OpenApiResponseMetadata
{
    /// <summary>Gets the response key (status code, range, or <c>default</c>).</summary>
    public required string StatusCode { get; init; }

    /// <summary>Gets the response description.</summary>
    public string? Description { get; init; }

    /// <summary>Gets the media type of the response body, if it has content.</summary>
    public string? ContentType { get; init; }

    /// <summary>Gets the resolved schema reference for the response body, if any.</summary>
    public string? SchemaReference { get; init; }

    /// <summary>
    /// Gets a complete schema for the response body, supplied by a producer that already holds one (the
    /// Web OpenAPI adapter, which derives schemas from the application's serialization contracts). When
    /// set together with <see cref="ContentType"/>, generation uses it as the media type's schema and
    /// ignores <see cref="SchemaReference"/>. The attribute mapper and the source generator never set it.
    /// </summary>
    public OpenApiSchema? Schema { get; init; }
}

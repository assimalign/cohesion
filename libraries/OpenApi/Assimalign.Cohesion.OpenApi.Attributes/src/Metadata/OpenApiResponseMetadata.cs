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
}

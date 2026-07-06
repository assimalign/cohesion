namespace Assimalign.Cohesion.OpenApi.Integration;

/// <summary>
/// The document-level metadata a description provider stamps onto the composed document — the parts that
/// come from the service, not from any individual endpoint source.
/// </summary>
public sealed class OpenApiDescriptionInfo
{
    /// <summary>Gets the API title (a required Info field).</summary>
    public required string Title { get; init; }

    /// <summary>Gets the API version (a required Info field), distinct from the OpenAPI line.</summary>
    public required string ApiVersion { get; init; }

    /// <summary>Gets an optional API description.</summary>
    public string? Description { get; init; }
}

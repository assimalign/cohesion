namespace Assimalign.Cohesion.OpenApi.Generation;

/// <summary>
/// Options controlling how a document is generated from metadata: the target OpenAPI line and the
/// required document metadata.
/// </summary>
public sealed class OpenApiGenerationOptions
{
    /// <summary>Gets the OpenAPI line the generated document targets. Defaults to <see cref="OpenApiSpecVersion.V3_1"/>.</summary>
    public OpenApiSpecVersion Version { get; init; } = OpenApiSpecVersion.V3_1;

    /// <summary>Gets the API title (a required Info field).</summary>
    public required string Title { get; init; }

    /// <summary>Gets the API version (a required Info field), distinct from the OpenAPI line.</summary>
    public required string ApiVersion { get; init; }

    /// <summary>Gets an optional API description.</summary>
    public string? Description { get; init; }
}

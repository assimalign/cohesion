using System.Collections.Generic;

namespace Assimalign.Cohesion.OpenApi;

/// <summary>
/// Metadata about the API. See the "Info Object" section of the OpenAPI Specification.
/// </summary>
public sealed class OpenApiInfo : IOpenApiExtensible
{
    /// <summary>Gets or sets the title of the API. Required by the specification.</summary>
    public string Title { get; set; } = string.Empty;

    /// <summary>Gets or sets a short summary of the API (OpenAPI 3.1+).</summary>
    public string? Summary { get; set; }

    /// <summary>Gets or sets a description of the API. CommonMark syntax may be used.</summary>
    public string? Description { get; set; }

    /// <summary>Gets or sets a URI for the Terms of Service for the API.</summary>
    public string? TermsOfService { get; set; }

    /// <summary>Gets or sets the contact information for the API.</summary>
    public OpenApiContact? Contact { get; set; }

    /// <summary>Gets or sets the license information for the API.</summary>
    public OpenApiLicense? License { get; set; }

    /// <summary>Gets or sets the version of the API description (distinct from the OpenAPI line). Required by the specification.</summary>
    public string Version { get; set; } = string.Empty;

    /// <inheritdoc/>
    public IDictionary<string, OpenApiNode> Extensions { get; } = new Dictionary<string, OpenApiNode>();
}

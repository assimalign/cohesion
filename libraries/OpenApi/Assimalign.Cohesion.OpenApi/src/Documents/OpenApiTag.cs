using System.Collections.Generic;

namespace Assimalign.Cohesion.OpenApi;

/// <summary>
/// Metadata for a single tag used by operations. See the "Tag Object" section of the OpenAPI Specification.
/// </summary>
public sealed class OpenApiTag : IOpenApiExtensible
{
    /// <summary>Gets or sets the name of the tag. Required by the specification.</summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>Gets or sets a short summary of the tag (OpenAPI 3.2+).</summary>
    public string? Summary { get; set; }

    /// <summary>Gets or sets a description for the tag. CommonMark syntax may be used.</summary>
    public string? Description { get; set; }

    /// <summary>Gets or sets the name of a parent tag, enabling a tag hierarchy (OpenAPI 3.2+).</summary>
    public string? Parent { get; set; }

    /// <summary>Gets or sets a machine-readable categorization hint for the tag (OpenAPI 3.2+).</summary>
    public string? Kind { get; set; }

    /// <summary>Gets or sets additional external documentation for the tag.</summary>
    public OpenApiExternalDocumentation? ExternalDocs { get; set; }

    /// <inheritdoc/>
    public IDictionary<string, OpenApiNode> Extensions { get; } = new Dictionary<string, OpenApiNode>();
}

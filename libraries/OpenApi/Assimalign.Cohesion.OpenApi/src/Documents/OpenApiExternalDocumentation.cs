using System.Collections.Generic;

namespace Assimalign.Cohesion.OpenApi;

/// <summary>
/// A reference to external documentation. See the "External Documentation Object" section of the OpenAPI Specification.
/// </summary>
public sealed class OpenApiExternalDocumentation : IOpenApiExtensible
{
    /// <summary>Gets or sets a description of the target documentation. CommonMark syntax may be used.</summary>
    public string? Description { get; set; }

    /// <summary>Gets or sets the URI for the target documentation. Required by the specification.</summary>
    public string Url { get; set; } = string.Empty;

    /// <inheritdoc/>
    public IDictionary<string, OpenApiNode> Extensions { get; } = new Dictionary<string, OpenApiNode>();
}

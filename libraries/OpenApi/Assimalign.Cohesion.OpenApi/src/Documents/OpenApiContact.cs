using System.Collections.Generic;

namespace Assimalign.Cohesion.OpenApi;

/// <summary>
/// Contact information for the API. See the "Contact Object" section of the OpenAPI Specification.
/// </summary>
public sealed class OpenApiContact : IOpenApiExtensible
{
    /// <summary>Gets or sets the identifying name of the contact person or organization.</summary>
    public string? Name { get; set; }

    /// <summary>Gets or sets the URI pointing to the contact information.</summary>
    public string? Url { get; set; }

    /// <summary>Gets or sets the email address of the contact person or organization.</summary>
    public string? Email { get; set; }

    /// <inheritdoc/>
    public IDictionary<string, OpenApiNode> Extensions { get; } = new Dictionary<string, OpenApiNode>();
}

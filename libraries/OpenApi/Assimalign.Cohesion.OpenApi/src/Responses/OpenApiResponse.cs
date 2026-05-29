using System.Collections.Generic;

namespace Assimalign.Cohesion.OpenApi;

/// <summary>
/// A single response from an API operation. See the "Response Object" section of the OpenAPI Specification.
/// </summary>
public sealed class OpenApiResponse : IOpenApiReferenceable, IOpenApiExtensible
{
    /// <inheritdoc/>
    public OpenApiReference? Reference { get; set; }

    /// <summary>Gets or sets a description of the response. Required by the specification. CommonMark syntax may be used.</summary>
    public string Description { get; set; } = string.Empty;

    /// <summary>Gets the headers returned with the response, keyed by header name.</summary>
    public IDictionary<string, OpenApiHeader> Headers { get; } = new Dictionary<string, OpenApiHeader>();

    /// <summary>Gets the content of the response, keyed by media type.</summary>
    public IDictionary<string, OpenApiMediaType> Content { get; } = new Dictionary<string, OpenApiMediaType>();

    /// <summary>Gets the operation links that can be followed from the response, keyed by a unique name.</summary>
    public IDictionary<string, OpenApiLink> Links { get; } = new Dictionary<string, OpenApiLink>();

    /// <inheritdoc/>
    public IDictionary<string, OpenApiNode> Extensions { get; } = new Dictionary<string, OpenApiNode>();
}

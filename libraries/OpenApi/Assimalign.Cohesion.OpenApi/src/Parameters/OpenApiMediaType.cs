using System.Collections.Generic;

namespace Assimalign.Cohesion.OpenApi;

/// <summary>
/// A media type definition for a request body or response. See the "Media Type Object" section of the
/// OpenAPI Specification.
/// </summary>
public sealed class OpenApiMediaType : IOpenApiExtensible
{
    /// <summary>Gets or sets the schema defining the content of the request, response, or parameter.</summary>
    public OpenApiSchema? Schema { get; set; }

    /// <summary>Gets or sets a single example of the media type.</summary>
    public OpenApiNode? Example { get; set; }

    /// <summary>Gets the examples of the media type, keyed by a unique name.</summary>
    public IDictionary<string, OpenApiExample> Examples { get; } = new Dictionary<string, OpenApiExample>();

    /// <summary>Gets the encoding information for properties of the media type, keyed by property name.</summary>
    public IDictionary<string, OpenApiEncoding> Encoding { get; } = new Dictionary<string, OpenApiEncoding>();

    /// <inheritdoc/>
    public IDictionary<string, OpenApiNode> Extensions { get; } = new Dictionary<string, OpenApiNode>();
}

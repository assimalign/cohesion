using System.Collections.Generic;

namespace Assimalign.Cohesion.OpenApi;

/// <summary>
/// A media type definition for a request body or response. See the "Media Type Object" section of the
/// OpenAPI Specification.
/// </summary>
public sealed class OpenApiMediaType : IOpenApiReferenceable, IOpenApiExtensible
{
    /// <inheritdoc/>
    /// <remarks>A <c>content</c> map value may be a Reference Object only from OpenAPI 3.2 onward.</remarks>
    public OpenApiReference? Reference { get; set; }

    /// <summary>Gets or sets the schema defining the content of the request, response, or parameter.</summary>
    public OpenApiSchema? Schema { get; set; }

    /// <summary>Gets or sets the schema applied independently to each item of a sequential media type such as <c>application/jsonl</c> or <c>text/event-stream</c> (OpenAPI 3.2+).</summary>
    public OpenApiSchema? ItemSchema { get; set; }

    /// <summary>Gets or sets a single example of the media type.</summary>
    public OpenApiNode? Example { get; set; }

    /// <summary>Gets the examples of the media type, keyed by a unique name.</summary>
    public IDictionary<string, OpenApiExample> Examples { get; } = new Dictionary<string, OpenApiExample>();

    /// <summary>Gets the encoding information for properties of the media type, keyed by property name. Mutually exclusive with <see cref="PrefixEncoding"/> and <see cref="ItemEncoding"/>.</summary>
    public IDictionary<string, OpenApiEncoding> Encoding { get; } = new Dictionary<string, OpenApiEncoding>();

    /// <summary>Gets the positional encodings for multipart array items (OpenAPI 3.2+). Mutually exclusive with <see cref="Encoding"/>.</summary>
    public IList<OpenApiEncoding> PrefixEncoding { get; } = new List<OpenApiEncoding>();

    /// <summary>Gets or sets the encoding applied to all multipart array items not covered by <see cref="PrefixEncoding"/> (OpenAPI 3.2+). Mutually exclusive with <see cref="Encoding"/>.</summary>
    public OpenApiEncoding? ItemEncoding { get; set; }

    /// <inheritdoc/>
    public IDictionary<string, OpenApiNode> Extensions { get; } = new Dictionary<string, OpenApiNode>();
}

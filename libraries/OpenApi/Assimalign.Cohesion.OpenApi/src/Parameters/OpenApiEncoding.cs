using System.Collections.Generic;

namespace Assimalign.Cohesion.OpenApi;

/// <summary>
/// A single encoding definition applied to a schema property. See the "Encoding Object" section of the
/// OpenAPI Specification.
/// </summary>
public sealed class OpenApiEncoding : IOpenApiExtensible
{
    /// <summary>Gets or sets the content type for encoding a specific property, as a comma-separated list of specific or wildcard media types.</summary>
    public string? ContentType { get; set; }

    /// <summary>Gets the additional headers describing the encoding, keyed by header name.</summary>
    public IDictionary<string, OpenApiHeader> Headers { get; } = new Dictionary<string, OpenApiHeader>();

    /// <summary>Gets the nested by-name encodings for nested multipart content, keyed by property name (OpenAPI 3.2+). Mutually exclusive with <see cref="PrefixEncoding"/> and <see cref="ItemEncoding"/>.</summary>
    public IDictionary<string, OpenApiEncoding> Encoding { get; } = new Dictionary<string, OpenApiEncoding>();

    /// <summary>Gets the nested positional encodings for nested multipart content (OpenAPI 3.2+). Mutually exclusive with <see cref="Encoding"/>.</summary>
    public IList<OpenApiEncoding> PrefixEncoding { get; } = new List<OpenApiEncoding>();

    /// <summary>Gets or sets the nested encoding applied to all remaining nested multipart items (OpenAPI 3.2+). Mutually exclusive with <see cref="Encoding"/>.</summary>
    public OpenApiEncoding? ItemEncoding { get; set; }

    /// <summary>Gets or sets how a specific property value is serialized depending on its type.</summary>
    public ParameterStyle? Style { get; set; }

    /// <summary>Gets or sets a value indicating whether array or object values generate separate parameters per item.</summary>
    public bool? Explode { get; set; }

    /// <summary>Gets or sets a value indicating whether reserved characters are allowed without percent-encoding.</summary>
    public bool AllowReserved { get; set; }

    /// <inheritdoc/>
    public IDictionary<string, OpenApiNode> Extensions { get; } = new Dictionary<string, OpenApiNode>();
}

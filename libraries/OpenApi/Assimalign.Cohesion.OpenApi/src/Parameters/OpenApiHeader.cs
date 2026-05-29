using System.Collections.Generic;

namespace Assimalign.Cohesion.OpenApi;

/// <summary>
/// A response or encoding header. See the "Header Object" section of the OpenAPI Specification. A header
/// follows the structure of a <see cref="OpenApiParameter"/> without the <c>name</c> and <c>in</c> fields.
/// </summary>
public sealed class OpenApiHeader : IOpenApiReferenceable, IOpenApiExtensible
{
    /// <inheritdoc/>
    public OpenApiReference? Reference { get; set; }

    /// <summary>Gets or sets a brief description of the header. CommonMark syntax may be used.</summary>
    public string? Description { get; set; }

    /// <summary>Gets or sets a value indicating whether this header is mandatory.</summary>
    public bool Required { get; set; }

    /// <summary>Gets or sets a value indicating whether the header is deprecated.</summary>
    public bool Deprecated { get; set; }

    /// <summary>Gets or sets a value indicating whether an empty value is allowed.</summary>
    public bool AllowEmptyValue { get; set; }

    /// <summary>Gets or sets how the header value is serialized.</summary>
    public ParameterStyle? Style { get; set; }

    /// <summary>Gets or sets a value indicating whether array or object values generate separate values per item.</summary>
    public bool? Explode { get; set; }

    /// <summary>Gets or sets a value indicating whether reserved characters are allowed without percent-encoding.</summary>
    public bool AllowReserved { get; set; }

    /// <summary>Gets or sets the schema defining the type used for the header.</summary>
    public OpenApiSchema? Schema { get; set; }

    /// <summary>Gets or sets a single example of the header value.</summary>
    public OpenApiNode? Example { get; set; }

    /// <summary>Gets the examples of the header value, keyed by a unique name.</summary>
    public IDictionary<string, OpenApiExample> Examples { get; } = new Dictionary<string, OpenApiExample>();

    /// <summary>Gets the content representing the header, keyed by media type. Mutually exclusive with <see cref="Schema"/>.</summary>
    public IDictionary<string, OpenApiMediaType> Content { get; } = new Dictionary<string, OpenApiMediaType>();

    /// <inheritdoc/>
    public IDictionary<string, OpenApiNode> Extensions { get; } = new Dictionary<string, OpenApiNode>();
}

using System.Collections.Generic;

namespace Assimalign.Cohesion.OpenApi;

/// <summary>
/// A request body. See the "Request Body Object" section of the OpenAPI Specification.
/// </summary>
public sealed class OpenApiRequestBody : IOpenApiReferenceable, IOpenApiExtensible
{
    /// <inheritdoc/>
    public OpenApiReference? Reference { get; set; }

    /// <summary>Gets or sets a brief description of the request body. CommonMark syntax may be used.</summary>
    public string? Description { get; set; }

    /// <summary>Gets the content of the request body, keyed by media type. Required by the specification.</summary>
    public IDictionary<string, OpenApiMediaType> Content { get; } = new Dictionary<string, OpenApiMediaType>();

    /// <summary>Gets or sets a value indicating whether the request body is required in the request.</summary>
    public bool Required { get; set; }

    /// <inheritdoc/>
    public IDictionary<string, OpenApiNode> Extensions { get; } = new Dictionary<string, OpenApiNode>();
}

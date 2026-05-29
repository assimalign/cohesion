using System.Collections.Generic;

namespace Assimalign.Cohesion.OpenApi;

/// <summary>
/// An example of a media type or parameter value. See the "Example Object" section of the OpenAPI Specification.
/// </summary>
public sealed class OpenApiExample : IOpenApiReferenceable, IOpenApiExtensible
{
    /// <inheritdoc/>
    public OpenApiReference? Reference { get; set; }

    /// <summary>Gets or sets a short description for the example.</summary>
    public string? Summary { get; set; }

    /// <summary>Gets or sets a long description for the example. CommonMark syntax may be used.</summary>
    public string? Description { get; set; }

    /// <summary>Gets or sets the embedded literal example value. Mutually exclusive with <see cref="ExternalValue"/>.</summary>
    public OpenApiNode? Value { get; set; }

    /// <summary>Gets or sets a URI that identifies a literal example. Mutually exclusive with <see cref="Value"/>.</summary>
    public string? ExternalValue { get; set; }

    /// <inheritdoc/>
    public IDictionary<string, OpenApiNode> Extensions { get; } = new Dictionary<string, OpenApiNode>();
}

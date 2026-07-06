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

    /// <summary>Gets or sets the embedded literal example value. Mutually exclusive with <see cref="ExternalValue"/>, <see cref="DataValue"/>, and <see cref="SerializedValue"/>.</summary>
    public OpenApiNode? Value { get; set; }

    /// <summary>Gets or sets an example of the data structure that must be valid against the relevant schema (OpenAPI 3.2+). Mutually exclusive with <see cref="Value"/>.</summary>
    public OpenApiNode? DataValue { get; set; }

    /// <summary>Gets or sets the serialized form of the example, including any encoding or escaping (OpenAPI 3.2+). Mutually exclusive with <see cref="Value"/> and <see cref="ExternalValue"/>.</summary>
    public string? SerializedValue { get; set; }

    /// <summary>Gets or sets a URI that identifies a literal example. Mutually exclusive with <see cref="Value"/> and <see cref="SerializedValue"/>.</summary>
    public string? ExternalValue { get; set; }

    /// <inheritdoc/>
    public IDictionary<string, OpenApiNode> Extensions { get; } = new Dictionary<string, OpenApiNode>();
}

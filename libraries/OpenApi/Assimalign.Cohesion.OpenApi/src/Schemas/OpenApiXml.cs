using System.Collections.Generic;

namespace Assimalign.Cohesion.OpenApi;

/// <summary>
/// Metadata to assist with the XML representation of a schema. See the "XML Object" section of the OpenAPI Specification.
/// </summary>
public sealed class OpenApiXml : IOpenApiExtensible
{
    /// <summary>Gets or sets the name of the element or attribute used for the described schema property.</summary>
    public string? Name { get; set; }

    /// <summary>Gets or sets the URI of the XML namespace definition.</summary>
    public string? Namespace { get; set; }

    /// <summary>Gets or sets the prefix to be used for the <see cref="Name"/>.</summary>
    public string? Prefix { get; set; }

    /// <summary>
    /// Gets or sets the XML node type produced for the schema (OpenAPI 3.2+). Mutually exclusive with the
    /// deprecated <see cref="Attribute"/> and <see cref="Wrapped"/> fields.
    /// </summary>
    public XmlNodeType? NodeType { get; set; }

    /// <summary>Gets or sets a value indicating whether the property is represented as an attribute rather than an element. Deprecated in OpenAPI 3.2 in favor of <see cref="NodeType"/>.</summary>
    public bool Attribute { get; set; }

    /// <summary>Gets or sets a value indicating whether an array is wrapped in a containing element. Deprecated in OpenAPI 3.2 in favor of <see cref="NodeType"/>.</summary>
    public bool Wrapped { get; set; }

    /// <inheritdoc/>
    public IDictionary<string, OpenApiNode> Extensions { get; } = new Dictionary<string, OpenApiNode>();
}

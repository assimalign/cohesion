using System.Collections.Generic;

namespace Assimalign.Cohesion.OpenApi;

/// <summary>
/// Implemented by OpenAPI elements that permit specification extensions — fields whose names
/// begin with <c>x-</c> (see the "Specification Extensions" section of the OpenAPI Specification).
/// </summary>
public interface IOpenApiExtensible : IOpenApiElement
{
    /// <summary>
    /// Gets the specification extensions declared on this element, keyed by their full field name
    /// (including the leading <c>x-</c> prefix). Values are arbitrary data represented as an
    /// <see cref="OpenApiNode"/> tree.
    /// </summary>
    IDictionary<string, OpenApiNode> Extensions { get; }
}

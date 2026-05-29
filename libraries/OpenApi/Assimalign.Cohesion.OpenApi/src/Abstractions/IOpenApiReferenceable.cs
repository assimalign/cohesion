namespace Assimalign.Cohesion.OpenApi;

/// <summary>
/// Implemented by OpenAPI elements that may be replaced by a Reference Object (<c>$ref</c>)
/// when they appear inside a document.
/// </summary>
/// <remarks>
/// When <see cref="Reference"/> is non-<see langword="null"/>, serializers emit a reference object
/// in place of the inline element. The inline properties remain available so that a resolver can
/// populate them after dereferencing.
/// </remarks>
public interface IOpenApiReferenceable : IOpenApiElement
{
    /// <summary>
    /// Gets or sets the reference that stands in for this element, or <see langword="null"/>
    /// when the element is declared inline.
    /// </summary>
    OpenApiReference? Reference { get; set; }
}

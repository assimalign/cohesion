using System.Collections.Generic;

namespace Assimalign.Cohesion.OpenApi;

/// <summary>
/// A map of possible out-of-band callbacks related to the parent operation. See the "Callback Object"
/// section of the OpenAPI Specification.
/// </summary>
/// <remarks>
/// Keys are runtime expressions that identify the URL to be used for the callback operation.
/// </remarks>
public sealed class OpenApiCallback : IOpenApiReferenceable, IOpenApiExtensible
{
    /// <inheritdoc/>
    public OpenApiReference? Reference { get; set; }

    /// <summary>Gets the path items that describe the callback request and expected responses, keyed by runtime expression.</summary>
    public IDictionary<string, OpenApiPathItem> PathItems { get; } = new Dictionary<string, OpenApiPathItem>();

    /// <inheritdoc/>
    public IDictionary<string, OpenApiNode> Extensions { get; } = new Dictionary<string, OpenApiNode>();
}

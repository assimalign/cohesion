using System.Collections.Generic;

namespace Assimalign.Cohesion.OpenApi;

/// <summary>
/// The operations available on a single path. See the "Path Item Object" section of the OpenAPI Specification.
/// </summary>
public sealed class OpenApiPathItem : IOpenApiReferenceable, IOpenApiExtensible
{
    /// <inheritdoc/>
    public OpenApiReference? Reference { get; set; }

    /// <summary>Gets or sets a short summary intended to apply to all operations in this path.</summary>
    public string? Summary { get; set; }

    /// <summary>Gets or sets a description intended to apply to all operations in this path. CommonMark syntax may be used.</summary>
    public string? Description { get; set; }

    /// <summary>Gets the operations on this path keyed by their standard HTTP method.</summary>
    public IDictionary<OperationType, OpenApiOperation> Operations { get; } = new Dictionary<OperationType, OpenApiOperation>();

    /// <summary>Gets the operations on this path keyed by non-standard HTTP method name (OpenAPI 3.2+).</summary>
    public IDictionary<string, OpenApiOperation> AdditionalOperations { get; } = new Dictionary<string, OpenApiOperation>();

    /// <summary>Gets the alternative servers that serve all operations in this path.</summary>
    public IList<OpenApiServer> Servers { get; } = new List<OpenApiServer>();

    /// <summary>Gets the parameters applicable to all operations described under this path.</summary>
    public IList<OpenApiParameter> Parameters { get; } = new List<OpenApiParameter>();

    /// <inheritdoc/>
    public IDictionary<string, OpenApiNode> Extensions { get; } = new Dictionary<string, OpenApiNode>();
}

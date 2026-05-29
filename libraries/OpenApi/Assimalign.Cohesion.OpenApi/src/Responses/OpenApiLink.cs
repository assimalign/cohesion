using System.Collections.Generic;

namespace Assimalign.Cohesion.OpenApi;

/// <summary>
/// A possible design-time link for a response. See the "Link Object" section of the OpenAPI Specification.
/// </summary>
public sealed class OpenApiLink : IOpenApiReferenceable, IOpenApiExtensible
{
    /// <inheritdoc/>
    public OpenApiReference? Reference { get; set; }

    /// <summary>Gets or sets a relative or absolute URI reference to an operation. Mutually exclusive with <see cref="OperationId"/>.</summary>
    public string? OperationRef { get; set; }

    /// <summary>Gets or sets the name of an existing, resolvable operation. Mutually exclusive with <see cref="OperationRef"/>.</summary>
    public string? OperationId { get; set; }

    /// <summary>Gets the parameters to pass to the linked operation, keyed by parameter name. Values are constants or runtime expressions.</summary>
    public IDictionary<string, OpenApiNode> Parameters { get; } = new Dictionary<string, OpenApiNode>();

    /// <summary>Gets or sets the request body to pass to the linked operation as a constant or runtime expression.</summary>
    public OpenApiNode? RequestBody { get; set; }

    /// <summary>Gets or sets a description of the link. CommonMark syntax may be used.</summary>
    public string? Description { get; set; }

    /// <summary>Gets or sets the server to be used by the linked operation.</summary>
    public OpenApiServer? Server { get; set; }

    /// <inheritdoc/>
    public IDictionary<string, OpenApiNode> Extensions { get; } = new Dictionary<string, OpenApiNode>();
}

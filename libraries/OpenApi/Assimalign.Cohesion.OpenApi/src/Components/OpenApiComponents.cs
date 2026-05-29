using System.Collections.Generic;

namespace Assimalign.Cohesion.OpenApi;

/// <summary>
/// A container for reusable objects referenced from elsewhere in the document. See the "Components Object"
/// section of the OpenAPI Specification.
/// </summary>
public sealed class OpenApiComponents : IOpenApiExtensible
{
    /// <summary>Gets the reusable schemas, keyed by component name.</summary>
    public IDictionary<string, OpenApiSchema> Schemas { get; } = new Dictionary<string, OpenApiSchema>();

    /// <summary>Gets the reusable responses, keyed by component name.</summary>
    public IDictionary<string, OpenApiResponse> Responses { get; } = new Dictionary<string, OpenApiResponse>();

    /// <summary>Gets the reusable parameters, keyed by component name.</summary>
    public IDictionary<string, OpenApiParameter> Parameters { get; } = new Dictionary<string, OpenApiParameter>();

    /// <summary>Gets the reusable examples, keyed by component name.</summary>
    public IDictionary<string, OpenApiExample> Examples { get; } = new Dictionary<string, OpenApiExample>();

    /// <summary>Gets the reusable request bodies, keyed by component name.</summary>
    public IDictionary<string, OpenApiRequestBody> RequestBodies { get; } = new Dictionary<string, OpenApiRequestBody>();

    /// <summary>Gets the reusable headers, keyed by component name.</summary>
    public IDictionary<string, OpenApiHeader> Headers { get; } = new Dictionary<string, OpenApiHeader>();

    /// <summary>Gets the reusable security schemes, keyed by component name.</summary>
    public IDictionary<string, OpenApiSecurityScheme> SecuritySchemes { get; } = new Dictionary<string, OpenApiSecurityScheme>();

    /// <summary>Gets the reusable links, keyed by component name.</summary>
    public IDictionary<string, OpenApiLink> Links { get; } = new Dictionary<string, OpenApiLink>();

    /// <summary>Gets the reusable callbacks, keyed by component name.</summary>
    public IDictionary<string, OpenApiCallback> Callbacks { get; } = new Dictionary<string, OpenApiCallback>();

    /// <summary>Gets the reusable path items, keyed by component name (OpenAPI 3.1+).</summary>
    public IDictionary<string, OpenApiPathItem> PathItems { get; } = new Dictionary<string, OpenApiPathItem>();

    /// <inheritdoc/>
    public IDictionary<string, OpenApiNode> Extensions { get; } = new Dictionary<string, OpenApiNode>();
}

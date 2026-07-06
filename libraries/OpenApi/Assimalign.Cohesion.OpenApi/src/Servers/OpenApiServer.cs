using System.Collections.Generic;

namespace Assimalign.Cohesion.OpenApi;

/// <summary>
/// Connectivity information for a target server. See the "Server Object" section of the OpenAPI Specification.
/// </summary>
public sealed class OpenApiServer : IOpenApiExtensible
{
    /// <summary>Gets or sets a URI to the target host, optionally containing <c>{variable}</c> template placeholders. Required by the specification. Query and fragment must not be part of the URL.</summary>
    public string Url { get; set; } = string.Empty;

    /// <summary>Gets or sets an optional unique string used to refer to the host designated by the URL (OpenAPI 3.2+).</summary>
    public string? Name { get; set; }

    /// <summary>Gets or sets a description of the host designated by the URL. CommonMark syntax may be used.</summary>
    public string? Description { get; set; }

    /// <summary>Gets the substitution variables referenced in the server <see cref="Url"/> template, keyed by variable name.</summary>
    public IDictionary<string, OpenApiServerVariable> Variables { get; } = new Dictionary<string, OpenApiServerVariable>();

    /// <inheritdoc/>
    public IDictionary<string, OpenApiNode> Extensions { get; } = new Dictionary<string, OpenApiNode>();
}

using System.Collections.Generic;

namespace Assimalign.Cohesion.OpenApi;

/// <summary>
/// A substitution variable for a server URL template. See the "Server Variable Object" section of the OpenAPI Specification.
/// </summary>
public sealed class OpenApiServerVariable : IOpenApiExtensible
{
    /// <summary>Gets the enumeration of string values to be used when the substitution options are limited.</summary>
    public IList<string> Enum { get; } = new List<string>();

    /// <summary>Gets or sets the default value to use for substitution. Required by the specification.</summary>
    public string Default { get; set; } = string.Empty;

    /// <summary>Gets or sets a description for the server variable. CommonMark syntax may be used.</summary>
    public string? Description { get; set; }

    /// <inheritdoc/>
    public IDictionary<string, OpenApiNode> Extensions { get; } = new Dictionary<string, OpenApiNode>();
}

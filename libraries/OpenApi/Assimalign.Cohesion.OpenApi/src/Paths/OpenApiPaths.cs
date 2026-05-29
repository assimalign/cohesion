using System.Collections.Generic;

namespace Assimalign.Cohesion.OpenApi;

/// <summary>
/// The relative paths to the individual endpoints and their operations. See the "Paths Object" section
/// of the OpenAPI Specification.
/// </summary>
/// <remarks>
/// Keys are path templates that must begin with a forward slash, for example <c>/pets/{petId}</c>.
/// </remarks>
public sealed class OpenApiPaths : IOpenApiExtensible
{
    /// <summary>Gets the path items, keyed by their path template.</summary>
    public IDictionary<string, OpenApiPathItem> Items { get; } = new Dictionary<string, OpenApiPathItem>();

    /// <summary>Gets or sets the path item for the specified path template.</summary>
    /// <param name="path">The path template, for example <c>/pets</c>.</param>
    /// <returns>The path item associated with <paramref name="path"/>.</returns>
    public OpenApiPathItem this[string path]
    {
        get => Items[path];
        set => Items[path] = value;
    }

    /// <inheritdoc/>
    public IDictionary<string, OpenApiNode> Extensions { get; } = new Dictionary<string, OpenApiNode>();
}

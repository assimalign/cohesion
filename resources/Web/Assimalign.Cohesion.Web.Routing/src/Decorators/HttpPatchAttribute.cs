using Assimalign.Cohesion.Http;

namespace Assimalign.Cohesion.Web.Routing;

/// <summary>
/// Marks an action as handling HTTP PATCH requests.
/// </summary>
public sealed class HttpPatchAttribute : HttpMethodRouteAttribute
{
    /// <summary>
    /// Creates a new HTTP PATCH route attribute.
    /// </summary>
    /// <param name="template">The optional route template.</param>
    public HttpPatchAttribute(string? template = null)
        : base(HttpMethod.Patch, template)
    {
    }
}

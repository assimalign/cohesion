using Assimalign.Cohesion.Http;
using System.Diagnostics.CodeAnalysis;

namespace Assimalign.Cohesion.Web.Routing;

/// <summary>
/// Marks an action as handling HTTP DELETE requests.
/// </summary>
public sealed class HttpDeleteAttribute : HttpMethodRouteAttribute
{
    /// <summary>
    /// Creates a new HTTP DELETE route attribute.
    /// </summary>
    /// <param name="template">The optional route template.</param>
    public HttpDeleteAttribute(string? template = null)
        : base(HttpMethod.Delete, template)
    {
    }
}

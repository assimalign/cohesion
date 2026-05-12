using Assimalign.Cohesion.Http;

namespace Assimalign.Cohesion.Web.Routing;

/// <summary>
/// Marks an action as handling HTTP GET requests.
/// </summary>
public sealed class HttpGetAttribute : HttpMethodRouteAttribute
{
    /// <summary>
    /// Creates a new HTTP GET route attribute.
    /// </summary>
    /// <param name="template">The optional route template.</param>
    public HttpGetAttribute(string? template = null)
        : base(HttpMethod.Get, template)
    {
    }
}

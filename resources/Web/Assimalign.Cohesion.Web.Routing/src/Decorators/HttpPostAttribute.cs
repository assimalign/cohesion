using Assimalign.Cohesion.Http;

namespace Assimalign.Cohesion.Web.Routing;

/// <summary>
/// Marks an action as handling HTTP POST requests.
/// </summary>
public sealed class HttpPostAttribute : HttpMethodRouteAttribute
{
    /// <summary>
    /// Creates a new HTTP POST route attribute.
    /// </summary>
    /// <param name="template">The optional route template.</param>
    public HttpPostAttribute(string? template = null)
        : base(HttpMethod.Post, template)
    {
    }
}

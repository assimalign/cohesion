using Assimalign.Cohesion.Http;

namespace Assimalign.Cohesion.Web.Routing;

/// <summary>
/// Marks an action as handling HTTP PUT requests.
/// </summary>
public sealed class HttpPutAttribute : HttpMethodRouteAttribute
{
    /// <summary>
    /// Creates a new HTTP PUT route attribute.
    /// </summary>
    /// <param name="template">The optional route template.</param>
    public HttpPutAttribute(string? template = null)
        : base(HttpMethod.Put, template)
    {
    }
}

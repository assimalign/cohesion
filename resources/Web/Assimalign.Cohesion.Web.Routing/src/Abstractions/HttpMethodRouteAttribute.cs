using System.Collections.Generic;

using Assimalign.Cohesion.Http;

namespace Assimalign.Cohesion.Web.Routing;

/// <summary>
/// Defines an HTTP-method-constrained route template for a controller action.
/// </summary>
public abstract class HttpMethodRouteAttribute : System.Attribute
{
    /// <summary>
    /// Creates a new HTTP method route attribute.
    /// </summary>
    /// <param name="method">The HTTP method supported by the action.</param>
    /// <param name="template">The optional route template.</param>
    protected HttpMethodRouteAttribute(HttpMethod method, string? template = null)
    {
        Methods = new[] { method };
        Template = template;
    }

    /// <summary>
    /// Gets the HTTP methods supported by the action.
    /// </summary>
    public IReadOnlyList<HttpMethod> Methods { get; }

    /// <summary>
    /// Gets the optional action route template.
    /// </summary>
    public string? Template { get; }

    /// <summary>
    /// Gets or sets the action route order used during endpoint projection.
    /// </summary>
    public int Order { get; set; }
}

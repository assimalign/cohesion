using System;

namespace Assimalign.Cohesion.Web.Routing;

/// <summary>
/// Defines a conventional route template for a controller or action.
/// </summary>
[AttributeUsage(AttributeTargets.Class | AttributeTargets.Method, AllowMultiple = true, Inherited = true)]
public sealed class HttpRouteAttribute : Attribute
{
    /// <summary>
    /// Creates a new route attribute.
    /// </summary>
    /// <param name="template">The route template.</param>
    public HttpRouteAttribute(string template)
    {
        ArgumentException.ThrowIfNullOrEmpty(template);
        Template = template;
    }

    /// <summary>
    /// Gets the route template.
    /// </summary>
    public string Template { get; }

    /// <summary>
    /// Gets or sets the route order used when projecting attribute routes into endpoint candidates.
    /// </summary>
    public int Order { get; set; }
}

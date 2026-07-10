using System;

namespace Assimalign.Cohesion.Web.Routing.Policies;

using Assimalign.Cohesion.Http;
using Assimalign.Cohesion.Web.Routing.Patterns;

/// <summary>
/// Provides the current route state to executable parameter policies.
/// </summary>
public sealed class RouteParameterPolicyContext
{
    /// <summary>
    /// Creates a new route parameter policy context.
    /// </summary>
    /// <param name="httpContext">The active HTTP context when routing an incoming request.</param>
    /// <param name="parameter">The parameter currently being evaluated.</param>
    /// <param name="values">The route values captured so far.</param>
    public RouteParameterPolicyContext(IHttpContext? httpContext, RoutePatternParameterSegment parameter, RouteValueDictionary values)
    {
        HttpContext = httpContext;
        Parameter = parameter ?? throw new ArgumentNullException(nameof(parameter));
        Values = values ?? throw new ArgumentNullException(nameof(values));
    }

    /// <summary>
    /// Gets the current HTTP context for the route evaluation, when routing an incoming request.
    /// </summary>
    public IHttpContext? HttpContext { get; }

    /// <summary>
    /// Gets the route parameter currently being evaluated.
    /// </summary>
    public RoutePatternParameterSegment Parameter { get; }

    /// <summary>
    /// Gets the name of the current route parameter.
    /// </summary>
    public string ParameterName => Parameter.Name;

    /// <summary>
    /// Gets the route values captured for the current route evaluation.
    /// </summary>
    public RouteValueDictionary Values { get; }

    /// <summary>
    /// Gets the current parameter value, when present.
    /// </summary>
    public object? ParameterValue => TryGetParameterValue(out object? value) ? value : null;

    /// <summary>
    /// Attempts to get a route value by key.
    /// </summary>
    /// <param name="routeKey">The route value key.</param>
    /// <param name="value">The resolved route value.</param>
    /// <returns><see langword="true"/> when the value exists; otherwise <see langword="false"/>.</returns>
    public bool TryGetRouteValue(string routeKey, out object? value)
    {
        ArgumentException.ThrowIfNullOrEmpty(routeKey);
        return Values.TryGetValue(routeKey, out value);
    }

    /// <summary>
    /// Attempts to get the current parameter value.
    /// </summary>
    /// <param name="value">The resolved parameter value.</param>
    /// <returns><see langword="true"/> when the parameter value exists; otherwise <see langword="false"/>.</returns>
    public bool TryGetParameterValue(out object? value)
    {
        return Values.TryGetValue(ParameterName, out value);
    }

    /// <summary>
    /// Replaces the current parameter's captured route value with <paramref name="value"/>. Typed
    /// policies (see <see cref="TypedRouteParameterPolicy"/>) call this to surface a strongly-typed
    /// value in place of the raw string once it has been parsed.
    /// </summary>
    /// <param name="value">The converted value to store for the current parameter.</param>
    public void SetParameterValue(object? value)
    {
        Values[ParameterName] = value;
    }
}

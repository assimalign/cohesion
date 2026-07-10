using System;
using System.Collections.Generic;

using Assimalign.Cohesion.Http;

namespace Assimalign.Cohesion.Web.Routing;

/// <summary>
/// Represents the result of matching an HTTP request against an <see cref="IRouter"/>.
/// </summary>
/// <remarks>
/// The result distinguishes three outcomes so a router — or middleware built over one — can produce
/// the correct response: a successful match, a path that matched with an unacceptable method
/// (405, carrying the set of acceptable methods for the <c>Allow</c> header), and no match at all (404).
/// </remarks>
public readonly struct RouteMatch
{
    private RouteMatch(
        RouteMatchStatus status,
        IRouterRoute? route,
        RouteValueDictionary values,
        IReadOnlyList<HttpMethod> allowedMethods)
    {
        Status = status;
        Route = route;
        Values = values;
        AllowedMethods = allowedMethods;
    }

    /// <summary>
    /// Gets the outcome of the match.
    /// </summary>
    public RouteMatchStatus Status { get; }

    /// <summary>
    /// Gets the matched route when <see cref="Status"/> is <see cref="RouteMatchStatus.Matched"/>; otherwise <see langword="null"/>.
    /// </summary>
    public IRouterRoute? Route { get; }

    /// <summary>
    /// Gets the captured route values when <see cref="Status"/> is <see cref="RouteMatchStatus.Matched"/>; otherwise an empty collection.
    /// </summary>
    public RouteValueDictionary Values { get; }

    /// <summary>
    /// Gets the HTTP methods acceptable for the matched path when <see cref="Status"/> is
    /// <see cref="RouteMatchStatus.MethodNotAllowed"/>; otherwise an empty collection.
    /// </summary>
    public IReadOnlyList<HttpMethod> AllowedMethods { get; }

    /// <summary>
    /// Gets a value indicating whether the request matched a route.
    /// </summary>
    public bool IsMatched => Status == RouteMatchStatus.Matched;

    /// <summary>
    /// A shared result representing no matching route (404 territory).
    /// </summary>
    public static readonly RouteMatch Unmatched = new(
        RouteMatchStatus.NoMatch,
        route: null,
        new RouteValueDictionary(),
        Array.Empty<HttpMethod>());

    /// <summary>
    /// Creates a successful match result.
    /// </summary>
    /// <param name="route">The matched route.</param>
    /// <param name="values">The captured route values.</param>
    /// <returns>A <see cref="RouteMatch"/> whose <see cref="Status"/> is <see cref="RouteMatchStatus.Matched"/>.</returns>
    /// <exception cref="ArgumentNullException">Thrown when <paramref name="route"/> or <paramref name="values"/> is <see langword="null"/>.</exception>
    public static RouteMatch Matched(IRouterRoute route, RouteValueDictionary values)
    {
        ArgumentNullException.ThrowIfNull(route);
        ArgumentNullException.ThrowIfNull(values);

        return new RouteMatch(RouteMatchStatus.Matched, route, values, Array.Empty<HttpMethod>());
    }

    /// <summary>
    /// Creates a method-mismatch (405) result.
    /// </summary>
    /// <param name="allowedMethods">The methods acceptable for the matched path.</param>
    /// <returns>A <see cref="RouteMatch"/> whose <see cref="Status"/> is <see cref="RouteMatchStatus.MethodNotAllowed"/>.</returns>
    /// <exception cref="ArgumentNullException">Thrown when <paramref name="allowedMethods"/> is <see langword="null"/>.</exception>
    public static RouteMatch MethodNotAllowed(IReadOnlyList<HttpMethod> allowedMethods)
    {
        ArgumentNullException.ThrowIfNull(allowedMethods);

        return new RouteMatch(RouteMatchStatus.MethodNotAllowed, route: null, new RouteValueDictionary(), allowedMethods);
    }

    /// <summary>
    /// Formats <see cref="AllowedMethods"/> as an RFC 9110 <c>Allow</c> header value (a comma-separated method list).
    /// </summary>
    /// <returns>The formatted header value, or <see cref="HttpHeaderValue.Empty"/> when no methods are present.</returns>
    public HttpHeaderValue ToAllowHeaderValue()
    {
        if (AllowedMethods.Count == 0)
        {
            return HttpHeaderValue.Empty;
        }

        string[] tokens = new string[AllowedMethods.Count];
        for (int i = 0; i < AllowedMethods.Count; i++)
        {
            tokens[i] = AllowedMethods[i].Value;
        }

        return new HttpHeaderValue(string.Join(", ", tokens));
    }
}

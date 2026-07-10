using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Threading;
using System.Threading.Tasks;

using Assimalign.Cohesion.Http;
using Assimalign.Cohesion.Web.Routing.Patterns;
using Assimalign.Cohesion.Web.Routing.Policies;

namespace Assimalign.Cohesion.Web.Routing;

/// <summary>
/// Represents a router that evaluates a set of routes against incoming HTTP requests.
/// </summary>
/// <remarks>
/// Candidates are evaluated in ascending <see cref="IRouterRoute.InboundPrecedence"/> order (more specific
/// routes first), with registration order breaking ties. This means a literal segment always wins over a
/// parameter segment regardless of the order the routes were registered. Method handling follows RFC 9110:
/// a path that matches with an unacceptable method yields <see cref="RouteMatchStatus.MethodNotAllowed"/>
/// (405) with the acceptable methods, and a <c>HEAD</c> request is served by a matching <c>GET</c> route
/// when <c>HEAD</c> is not explicitly mapped.
/// </remarks>
public sealed class Router : IRouter
{
    private readonly IReadOnlyList<IRouterRoute> _routes;
    private readonly IRouterRoute[] _ordered;

    /// <summary>
    /// Creates a new router from the supplied route collection.
    /// </summary>
    /// <param name="routes">The routes to evaluate.</param>
    /// <exception cref="ArgumentNullException">Thrown when <paramref name="routes"/> is <see langword="null"/>.</exception>
    public Router(IEnumerable<IRouterRoute> routes)
    {
        ArgumentNullException.ThrowIfNull(routes);

        _routes = routes.ToImmutableList();
        _ordered = OrderByPrecedence(_routes);
    }

    /// <summary>
    /// Creates a new router from a single route.
    /// </summary>
    /// <param name="route">The route to evaluate.</param>
    public Router(IRouterRoute route)
        : this(new[] { route ?? throw new ArgumentNullException(nameof(route)) })
    {
    }

    /// <summary>
    /// Creates a new router from a single raw route pattern.
    /// </summary>
    /// <param name="method">The HTTP method accepted by the route.</param>
    /// <param name="pattern">The raw route pattern.</param>
    public Router(HttpMethod method, string pattern)
        : this(new Route(method, pattern))
    {
    }

    /// <summary>
    /// Creates a new router from a single raw route pattern and custom policy map.
    /// </summary>
    /// <param name="method">The HTTP method accepted by the route.</param>
    /// <param name="pattern">The raw route pattern.</param>
    /// <param name="policyMap">The policy map used to resolve route parameter policies.</param>
    public Router(HttpMethod method, string pattern, RouteParameterPolicyMap policyMap)
        : this(new Route(method, pattern, policyMap))
    {
    }

    /// <summary>
    /// Creates a new router from a single parsed route pattern.
    /// </summary>
    /// <param name="method">The HTTP method accepted by the route.</param>
    /// <param name="pattern">The parsed route pattern.</param>
    public Router(HttpMethod method, RoutePattern pattern)
        : this(new Route(method, pattern))
    {
    }

    /// <summary>
    /// Creates a new router from a single parsed route pattern and custom policy map.
    /// </summary>
    /// <param name="method">The HTTP method accepted by the route.</param>
    /// <param name="pattern">The parsed route pattern.</param>
    /// <param name="policyMap">The policy map used to resolve route parameter policies.</param>
    public Router(HttpMethod method, RoutePattern pattern, RouteParameterPolicyMap policyMap)
        : this(new Route(method, pattern, policyMap))
    {
    }

    /// <summary>
    /// Gets the routes evaluated by the router, in registration order.
    /// </summary>
    public IReadOnlyList<IRouterRoute> Routes => _routes;

    IEnumerable<IRouterRoute> IRouter.Routes => Routes;

    /// <inheritdoc />
    public RouteMatch Match(IHttpContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        HttpMethod method = context.Request.Method;
        List<HttpMethod>? allowed = null;

        for (int i = 0; i < _ordered.Length; i++)
        {
            IRouterRoute candidate = _ordered[i];

            if (!candidate.TryMatchPath(context, out RouteValueDictionary values))
            {
                continue;
            }

            if (AcceptsMethod(candidate, method))
            {
                return RouteMatch.Matched(candidate, values);
            }

            // Path matched but the method did not — remember the acceptable methods in case no
            // higher- or lower-precedence candidate ends up matching the method (a 405, not a 404).
            allowed ??= new List<HttpMethod>();
            AccumulateAllowedMethods(candidate, allowed);
        }

        if (allowed is not null)
        {
            AddImpliedMethods(allowed);
            return RouteMatch.MethodNotAllowed(allowed);
        }

        return RouteMatch.Unmatched;
    }

    /// <summary>
    /// Attempts to match the current request against the configured routes.
    /// </summary>
    /// <param name="context">The HTTP context to evaluate.</param>
    /// <param name="route">The matched route when a candidate succeeds.</param>
    /// <param name="values">The matched route values.</param>
    /// <returns><see langword="true"/> when a route matches; otherwise <see langword="false"/>.</returns>
    public bool TryMatch(IHttpContext context, out IRouterRoute? route, out RouteValueDictionary values)
    {
        RouteMatch match = Match(context);

        if (match.Status == RouteMatchStatus.Matched)
        {
            route = match.Route;
            values = match.Values;
            return true;
        }

        route = null;
        values = new RouteValueDictionary();
        return false;
    }

    /// <inheritdoc />
    public Task RouteAsync(IHttpContext context, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(context);

        RouteMatch match = Match(context);

        switch (match.Status)
        {
            case RouteMatchStatus.Matched:
                context.SetRouteMatch(match.Route!, match.Values);
                return match.Route!.Handler.InvokeAsync(context, cancellationToken);

            case RouteMatchStatus.MethodNotAllowed:
                ApplyMethodNotAllowed(context, match);
                return Task.CompletedTask;

            default:
                return Task.CompletedTask;
        }
    }

    /// <summary>
    /// Writes a 405 status code and an <c>Allow</c> header describing the acceptable methods for the matched path.
    /// </summary>
    /// <param name="context">The HTTP context whose response is updated.</param>
    /// <param name="match">A <see cref="RouteMatchStatus.MethodNotAllowed"/> match result.</param>
    internal static void ApplyMethodNotAllowed(IHttpContext context, RouteMatch match)
    {
        context.Response.StatusCode = HttpStatusCode.MethodNotAllowed;
        context.Response.Headers[HttpHeaderKey.Allow] = match.ToAllowHeaderValue();
    }

    private static bool AcceptsMethod(IRouterRoute route, HttpMethod method)
    {
        IReadOnlyCollection<HttpMethod> methods = route.Methods;

        if (methods.Count == 0)
        {
            return true;
        }

        foreach (HttpMethod accepted in methods)
        {
            if (accepted == method)
            {
                return true;
            }
        }

        // RFC 9110 §9.3.2: a HEAD request is served identically to GET. A route that maps GET but not
        // HEAD therefore accepts HEAD, letting the handler run with the response body ultimately elided.
        if (method == HttpMethod.Head)
        {
            foreach (HttpMethod accepted in methods)
            {
                if (accepted == HttpMethod.Get)
                {
                    return true;
                }
            }
        }

        return false;
    }

    private static void AccumulateAllowedMethods(IRouterRoute route, List<HttpMethod> allowed)
    {
        foreach (HttpMethod method in route.Methods)
        {
            if (!ContainsMethod(allowed, method))
            {
                allowed.Add(method);
            }
        }
    }

    private static void AddImpliedMethods(List<HttpMethod> allowed)
    {
        // A GET-capable resource also answers HEAD, so advertise it in the Allow header.
        if (ContainsMethod(allowed, HttpMethod.Get) && !ContainsMethod(allowed, HttpMethod.Head))
        {
            allowed.Add(HttpMethod.Head);
        }
    }

    private static bool ContainsMethod(List<HttpMethod> methods, HttpMethod method)
    {
        for (int i = 0; i < methods.Count; i++)
        {
            if (methods[i] == method)
            {
                return true;
            }
        }

        return false;
    }

    private static IRouterRoute[] OrderByPrecedence(IReadOnlyList<IRouterRoute> routes)
    {
        int count = routes.Count;
        var ordered = new IRouterRoute[count];
        var keys = new PrecedenceKey[count];

        for (int i = 0; i < count; i++)
        {
            ordered[i] = routes[i];
            keys[i] = new PrecedenceKey(routes[i].InboundPrecedence, i);
        }

        // Sort by precedence ascending (more specific first), registration index breaking ties so the
        // ordering is deterministic and stable.
        Array.Sort(keys, ordered);
        return ordered;
    }

    private readonly struct PrecedenceKey : IComparable<PrecedenceKey>
    {
        public PrecedenceKey(decimal precedence, int index)
        {
            Precedence = precedence;
            Index = index;
        }

        public decimal Precedence { get; }

        public int Index { get; }

        public int CompareTo(PrecedenceKey other)
        {
            int result = Precedence.CompareTo(other.Precedence);
            return result != 0 ? result : Index.CompareTo(other.Index);
        }
    }
}

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
public sealed class Router : IRouter
{
    private readonly IReadOnlyList<IRouterRoute> _routes;

    /// <summary>
    /// Creates a new router from the supplied route collection.
    /// </summary>
    /// <param name="routes">The routes to evaluate.</param>
    public Router(IEnumerable<IRouterRoute> routes)
    {
        ArgumentNullException.ThrowIfNull(routes);

        _routes = routes.ToImmutableList();
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
    /// Gets the routes evaluated by the router.
    /// </summary>
    public IReadOnlyList<IRouterRoute> Routes => _routes;

    IEnumerable<IRouterRoute> IRouter.Routes => Routes;

    /// <summary>
    /// Attempts to match the current request against the configured routes.
    /// </summary>
    /// <param name="context">The HTTP context to evaluate.</param>
    /// <param name="route">The matched route when a candidate succeeds.</param>
    /// <param name="values">The matched route values.</param>
    /// <returns><see langword="true"/> when a route matches; otherwise <see langword="false"/>.</returns>
    public bool TryMatch(IHttpContext context, out IRouterRoute? route, out RouteValueDictionary values)
    {
        ArgumentNullException.ThrowIfNull(context);

        for (int i = 0; i < _routes.Count; i++)
        {
            IRouterRoute candidate = _routes[i];

            if (candidate.TryMatch(context, out values))
            {
                route = candidate;
                return true;
            }
        }

        route = null;
        values = new RouteValueDictionary();
        return false;
    }

    /// <inheritdoc />
    public Task RouteAsync(IHttpContext context, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(context);

        if (TryMatch(context, out IRouterRoute? route, out RouteValueDictionary values) && route is not null)
        {
            context.SetRouteMatch(route, values);
            return route.Handler.InvokeAsync(context, cancellationToken);
        }

        return Task.CompletedTask;
    }
}

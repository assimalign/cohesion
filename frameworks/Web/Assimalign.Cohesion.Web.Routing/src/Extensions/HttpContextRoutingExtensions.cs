using System;

using Assimalign.Cohesion.Http;

namespace Assimalign.Cohesion.Web.Routing;

/// <summary>
/// Provides access to matched routing data stored on an HTTP context.
/// </summary>
public static class HttpContextRoutingExtensions
{
    private const string RouteItemKey = "Assimalign.Cohesion.Web.Routing.Route";
    private const string RouteValuesItemKey = "Assimalign.Cohesion.Web.Routing.RouteValues";

    extension(IHttpContext context)
    {
        /// <summary>
        /// Stores the matched route and route values on the current HTTP context.
        /// </summary>
        /// <param name="route">The matched route.</param>
        /// <param name="values">The matched route values.</param>
        public void SetRouteMatch(IRouterRoute route, RouteValueDictionary values)
        {
            ArgumentNullException.ThrowIfNull(context);
            ArgumentNullException.ThrowIfNull(route);
            ArgumentNullException.ThrowIfNull(values);

            context.Items[RouteItemKey] = route;
            context.Items[RouteValuesItemKey] = values;
        }

        /// <summary>
        /// Attempts to get the matched route for the current request.
        /// </summary>
        /// <param name="route">The matched route.</param>
        /// <returns><see langword="true"/> when a matched route exists; otherwise <see langword="false"/>.</returns>
        public bool TryGetRoute(out IRouterRoute? route)
        {
            ArgumentNullException.ThrowIfNull(context);

            if (context.Items.TryGetValue(RouteItemKey, out object? value) && value is IRouterRoute matchedRoute)
            {
                route = matchedRoute;
                return true;
            }

            route = null;
            return false;
        }

        /// <summary>
        /// Attempts to get the matched route values for the current request.
        /// </summary>
        /// <param name="values">The matched route values.</param>
        /// <returns><see langword="true"/> when matched route values exist; otherwise <see langword="false"/>.</returns>
        public bool TryGetRouteValues(out RouteValueDictionary? values)
        {
            ArgumentNullException.ThrowIfNull(context);

            if (context.Items.TryGetValue(RouteValuesItemKey, out object? value) && value is RouteValueDictionary routeValues)
            {
                values = routeValues;
                return true;
            }

            values = null;
            return false;
        }
    }
}

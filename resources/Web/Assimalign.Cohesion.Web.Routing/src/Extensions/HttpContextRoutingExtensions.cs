using System;

using Assimalign.Cohesion.Http;
using Assimalign.Cohesion.Web.Routing.Metadata;

namespace Assimalign.Cohesion.Web.Routing;

/// <summary>
/// Provides access to matched routing data through the strongly-typed
/// <see cref="IRouteMatchFeature"/> on an HTTP context.
/// </summary>
/// <remarks>
/// Route match state (the matched route, its captured values, and its endpoint
/// metadata) lives in the type-keyed <see cref="IHttpContext.Features"/>
/// collection rather than under magic-string keys in
/// <see cref="IHttpContext.Items"/>. Consumers &#8211; authorization, content
/// negotiation, diagnostics, results and tooling &#8211; resolve it by contract
/// type, which keeps discovery reflection-free and AOT-safe.
/// </remarks>
public static class HttpContextRoutingExtensions
{
    extension(IHttpContext context)
    {
        /// <summary>
        /// Stores the matched route and route values on the current HTTP context
        /// as an <see cref="IRouteMatchFeature"/>. Replaces any previously stored match.
        /// </summary>
        /// <param name="route">The matched route.</param>
        /// <param name="values">The matched route values.</param>
        /// <exception cref="ArgumentNullException"><paramref name="route"/> or <paramref name="values"/> is <see langword="null"/>.</exception>
        public void SetRouteMatch(IRouterRoute route, RouteValueDictionary values)
        {
            ArgumentNullException.ThrowIfNull(context);
            ArgumentNullException.ThrowIfNull(route);
            ArgumentNullException.ThrowIfNull(values);

            // Replace-on-set: the feature collection is name-keyed, and every RouteMatchFeature
            // reports the same stable Name (nameof(IRouteMatchFeature)), so re-routing overwrites
            // the prior match in the same slot rather than accumulating stale entries.
            context.Features.Set<IRouteMatchFeature>(new RouteMatchFeature(route, values));
        }

        /// <summary>
        /// Gets the route-match feature for the current request, or
        /// <see langword="null"/> when no route has matched.
        /// </summary>
        /// <returns>The installed <see cref="IRouteMatchFeature"/>, or <see langword="null"/>.</returns>
        public IRouteMatchFeature? GetRouteMatch()
        {
            ArgumentNullException.ThrowIfNull(context);

            return context.Features.Get<IRouteMatchFeature>();
        }

        /// <summary>
        /// Attempts to get the matched route for the current request.
        /// </summary>
        /// <param name="route">The matched route.</param>
        /// <returns><see langword="true"/> when a matched route exists; otherwise <see langword="false"/>.</returns>
        public bool TryGetRoute(out IRouterRoute? route)
        {
            ArgumentNullException.ThrowIfNull(context);

            route = context.Features.Get<IRouteMatchFeature>()?.Route;
            return route is not null;
        }

        /// <summary>
        /// Attempts to get the matched route values for the current request.
        /// </summary>
        /// <param name="values">The matched route values.</param>
        /// <returns><see langword="true"/> when matched route values exist; otherwise <see langword="false"/>.</returns>
        public bool TryGetRouteValues(out RouteValueDictionary? values)
        {
            ArgumentNullException.ThrowIfNull(context);

            values = context.Features.Get<IRouteMatchFeature>()?.Values;
            return values is not null;
        }

        /// <summary>
        /// Gets the endpoint metadata of the route matched for the current request.
        /// </summary>
        /// <returns>
        /// The matched route's metadata, or <see cref="RouterRouteMetadataCollection.Empty"/>
        /// when no route has matched. Never <see langword="null"/>.
        /// </returns>
        public IRouterRouteMetadataCollection GetEndpointMetadata()
        {
            ArgumentNullException.ThrowIfNull(context);

            return context.Features.Get<IRouteMatchFeature>()?.Metadata ?? RouterRouteMetadataCollection.Empty;
        }

        /// <summary>
        /// Gets the most recently registered endpoint-metadata item assignable to
        /// <typeparamref name="TMetadata"/> for the route matched for the current
        /// request, or <see langword="null"/> when none is present (including when
        /// no route has matched).
        /// </summary>
        /// <typeparam name="TMetadata">The metadata contract or concrete type to resolve.</typeparam>
        /// <returns>The resolved metadata item, or <see langword="null"/>.</returns>
        public TMetadata? GetEndpointMetadata<TMetadata>() where TMetadata : class
        {
            ArgumentNullException.ThrowIfNull(context);

            return context.Features.Get<IRouteMatchFeature>()?.Metadata.GetMetadata<TMetadata>();
        }

        /// <summary>
        /// Gets the link generator for the application the current request belongs to, used to
        /// generate outbound URLs from named routes and route values.
        /// </summary>
        /// <returns>The application's <see cref="ILinkGenerator"/>.</returns>
        /// <exception cref="ArgumentNullException"><paramref name="context"/> is <see langword="null"/>.</exception>
        /// <exception cref="InvalidOperationException">
        /// Routing has not been registered on the application (call <c>AddRouting</c> on the web
        /// application builder).
        /// </exception>
        public ILinkGenerator GetLinkGenerator()
        {
            ArgumentNullException.ThrowIfNull(context);

            IRouterFeature feature = context.Features.Get<IRouterFeature>()
                ?? throw new InvalidOperationException(
                    "Routing has not been registered. Call AddRouting() on the web application builder before generating links.");

            return feature.Router.LinkGenerator;
        }
    }
}

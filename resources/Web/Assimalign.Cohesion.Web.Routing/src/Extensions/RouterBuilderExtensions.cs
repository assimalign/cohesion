using System;
using System.Collections.Generic;

using Assimalign.Cohesion.Http;
using Assimalign.Cohesion.Web.Routing.Internal;
using Assimalign.Cohesion.Web.Routing.Patterns;
using Assimalign.Cohesion.Web.Routing.Policies;

namespace Assimalign.Cohesion.Web.Routing;

/// <summary>
/// Route mapping and route-group composition extensions for <see cref="IRouterBuilder"/>.
/// </summary>
public static class RouterBuilderExtensions
{
    extension(IRouterBuilder builder)
    {
        /// <summary>
        /// Creates a route group that composes <paramref name="prefix"/>, shared parameter
        /// policies, and shared endpoint metadata onto child routes.
        /// </summary>
        /// <param name="prefix">
        /// The route-template prefix applied to child routes. May contain parameters (for example
        /// <c>{tenant}/api</c>) and may be empty to share only policies and metadata.
        /// </param>
        /// <returns>A route group builder mapping composed child routes into this router builder.</returns>
        /// <exception cref="ArgumentNullException">
        /// <paramref name="builder"/> or <paramref name="prefix"/> is <see langword="null"/>.
        /// </exception>
        /// <exception cref="Exceptions.RoutePatternException">
        /// <paramref name="prefix"/> is not a valid route template.
        /// </exception>
        /// <remarks>
        /// Each child route registered through the group is stored as a single fully-composed
        /// route — the router evaluates it exactly like a directly-mapped route, with no
        /// per-request prefix matching. See <see cref="IRouterGroupBuilder"/> for composition,
        /// override, and ordering semantics.
        /// </remarks>
        public IRouterGroupBuilder MapGroup(string prefix)
        {
            ArgumentNullException.ThrowIfNull(builder);
            ArgumentNullException.ThrowIfNull(prefix);

            return new RouterGroupBuilder(builder, parent: null, prefix);
        }

        /// <summary>
        /// Maps a route from a template and returns its builder, through which route-level metadata
        /// attaches until the route table is built.
        /// </summary>
        /// <param name="method">The HTTP method accepted by the route.</param>
        /// <param name="template">The route template.</param>
        /// <param name="handler">The handler invoked when the route matches.</param>
        /// <returns>The mapped route's builder.</returns>
        /// <exception cref="ArgumentNullException">
        /// <paramref name="builder"/>, <paramref name="template"/>, or <paramref name="handler"/> is <see langword="null"/>.
        /// </exception>
        /// <exception cref="Exceptions.RoutePatternException"><paramref name="template"/> is not a valid route template.</exception>
        /// <exception cref="InvalidOperationException">
        /// The route table has already been built, or the template references an unknown inline policy.
        /// </exception>
        public IRouterRouteBuilder Map(HttpMethod method, string template, IRouterRouteHandler handler)
        {
            return builder.Map(new[] { method }, template, handler);
        }

        /// <summary>
        /// Maps a route accepting multiple HTTP methods from a template and returns its builder,
        /// through which route-level metadata attaches until the route table is built.
        /// </summary>
        /// <param name="methods">The HTTP methods accepted by the route. An empty sequence accepts any method.</param>
        /// <param name="template">The route template.</param>
        /// <param name="handler">The handler invoked when the route matches.</param>
        /// <returns>The mapped route's builder.</returns>
        /// <exception cref="ArgumentNullException">
        /// <paramref name="builder"/>, <paramref name="methods"/>, <paramref name="template"/>, or
        /// <paramref name="handler"/> is <see langword="null"/>.
        /// </exception>
        /// <exception cref="Exceptions.RoutePatternException"><paramref name="template"/> is not a valid route template.</exception>
        /// <exception cref="InvalidOperationException">
        /// The route table has already been built, or the template references an unknown inline policy.
        /// </exception>
        public IRouterRouteBuilder Map(IEnumerable<HttpMethod> methods, string template, IRouterRouteHandler handler)
        {
            ArgumentNullException.ThrowIfNull(builder);
            ArgumentNullException.ThrowIfNull(methods);
            ArgumentNullException.ThrowIfNull(template);
            ArgumentNullException.ThrowIfNull(handler);

            DeferredRouteMetadata metadata = new(group: null, initial: null);
            builder.Map(new Route(methods, RoutePatternParser.Parse(template), RouteParameterPolicyMap.CreateDefault(), handler, metadata));

            return new RouterRouteBuilder(metadata);
        }

        /// <summary>
        /// Maps the application's fallback route (<c>{**path:nonfile}</c>): it answers <c>GET</c> (and
        /// <c>HEAD</c>) requests whose path no other route matches and whose last segment names no file.
        /// </summary>
        /// <remarks>
        /// <para>
        /// A fallback route is evaluated after every other route, whatever their precedence, so it never
        /// shadows an application route. The <c>nonfile</c> constraint keeps it from answering a request for a
        /// missing asset (<c>/app.js</c>), which still reaches the 404. It never turns an unmatched path into a
        /// 405: a <c>POST</c> to a path only the fallback matched is a 404.
        /// </para>
        /// <para>
        /// A single-page application serves its <c>index.html</c> this way (<c>MapFallbackToFile</c> in
        /// <c>Web.StaticFiles</c>), so client-side routes survive a page reload.
        /// </para>
        /// </remarks>
        /// <param name="handler">The handler invoked for requests the fallback answers.</param>
        /// <returns>The fallback route's builder.</returns>
        /// <exception cref="ArgumentNullException"><paramref name="builder"/> or <paramref name="handler"/> is <see langword="null"/>.</exception>
        /// <exception cref="InvalidOperationException">The route table has already been built.</exception>
        public IRouterRouteBuilder MapFallback(IRouterRouteHandler handler)
        {
            return builder.MapFallback("{**path:nonfile}", handler);
        }

        /// <summary>
        /// Maps a fallback route with its own template, for example <c>admin/{**path:nonfile}</c> for a
        /// fallback confined to a sub-path. It answers <c>GET</c> (and <c>HEAD</c>) requests the template
        /// matches when no other route does.
        /// </summary>
        /// <remarks>
        /// Evaluated after every non-fallback route and never part of a 405, like <see cref="MapFallback(IRouterRouteHandler)"/>.
        /// Fallback routes rank among themselves by ordinary precedence, so a sub-path fallback wins over the
        /// application-wide one for its paths. Use the <c>nonfile</c> constraint on the template's catch-all
        /// to keep missing assets a 404.
        /// </remarks>
        /// <param name="template">The fallback route's template.</param>
        /// <param name="handler">The handler invoked for requests the fallback answers.</param>
        /// <returns>The fallback route's builder.</returns>
        /// <exception cref="ArgumentNullException">
        /// <paramref name="builder"/>, <paramref name="template"/>, or <paramref name="handler"/> is <see langword="null"/>.
        /// </exception>
        /// <exception cref="Exceptions.RoutePatternException"><paramref name="template"/> is not a valid route template.</exception>
        /// <exception cref="InvalidOperationException">The route table has already been built.</exception>
        public IRouterRouteBuilder MapFallback(string template, IRouterRouteHandler handler)
        {
            ArgumentNullException.ThrowIfNull(builder);
            ArgumentNullException.ThrowIfNull(template);
            ArgumentNullException.ThrowIfNull(handler);

            return builder.Map(HttpMethod.Get, template, handler).WithMetadata(RouteFallbackMetadata.Instance);
        }
    }
}

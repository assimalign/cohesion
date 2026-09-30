using System;

namespace Assimalign.Cohesion.Web;

using Assimalign.Cohesion.Http;
using Assimalign.Cohesion.Web.Internal;
using Assimalign.Cohesion.Web.Routing;

/// <summary>
/// Endpoint mapping helpers for route groups: the same verbs as the application's
/// (<see cref="WebApplicationPipelineBuilderExtensions"/>), mapping into the group so each endpoint
/// composes the group's prefix, metadata and parameter policies.
/// </summary>
/// <remarks>
/// The <see cref="Delegate"/> overloads are placeholders the Cohesion Web source generator
/// (<c>Assimalign.Cohesion.SourceGeneration.Web</c>) intercepts, exactly as on the application, so a group
/// holds typed endpoints: <c>api.MapGet("orders/{id:int}", (int id, IHttpContext context) =&gt; ...)</c>.
/// </remarks>
public static class RouterGroupBuilderEndpointExtensions
{
    extension(IRouterGroupBuilder group)
    {
        /// <summary>
        /// Maps a route, relative to the group prefix, to the supplied terminal middleware.
        /// </summary>
        /// <param name="method">The HTTP method the route matches.</param>
        /// <param name="pattern">The route pattern, relative to the group prefix. An empty string maps the prefix itself.</param>
        /// <param name="middleware">The middleware to execute when the route matches.</param>
        /// <returns>The mapped route's builder, for attaching endpoint metadata.</returns>
        /// <exception cref="ArgumentNullException"><paramref name="group"/>, <paramref name="pattern"/> or <paramref name="middleware"/> is <see langword="null"/>.</exception>
        /// <exception cref="Routing.Exceptions.RoutePatternException">The composed template is not a valid route template.</exception>
        /// <exception cref="InvalidOperationException">The composed template references an unknown inline policy.</exception>
        public IRouterRouteBuilder Map(HttpMethod method, string pattern, WebApplicationMiddleware middleware)
        {
            ArgumentNullException.ThrowIfNull(group);
            ArgumentNullException.ThrowIfNull(pattern);
            ArgumentNullException.ThrowIfNull(middleware);

            return group.Map(method, pattern, new RouterRouteHandler(middleware));
        }

        /// <summary>
        /// Maps a GET route, relative to the group prefix, to the supplied terminal middleware.
        /// </summary>
        /// <param name="pattern">The route pattern, relative to the group prefix.</param>
        /// <param name="middleware">The middleware to execute when the route matches.</param>
        /// <returns>The mapped route's builder, for attaching endpoint metadata.</returns>
        /// <exception cref="ArgumentNullException"><paramref name="group"/>, <paramref name="pattern"/> or <paramref name="middleware"/> is <see langword="null"/>.</exception>
        /// <exception cref="Routing.Exceptions.RoutePatternException">The composed template is not a valid route template.</exception>
        public IRouterRouteBuilder MapGet(string pattern, WebApplicationMiddleware middleware)
            => group.Map(HttpMethod.Get, pattern, middleware);

        /// <summary>
        /// Maps a route, relative to the group prefix, to a typed handler whose parameters are bound
        /// from the request. The Cohesion Web source generator intercepts this call.
        /// </summary>
        /// <param name="method">The HTTP method the route matches.</param>
        /// <param name="pattern">The route pattern, relative to the group prefix.</param>
        /// <param name="handler">The typed handler lambda whose parameters are bound from the request.</param>
        /// <returns>The mapped route's builder, for attaching endpoint metadata.</returns>
        /// <exception cref="NotSupportedException">Always thrown when the source generator did not rewrite the call site.</exception>
        public IRouterRouteBuilder Map(HttpMethod method, string pattern, Delegate handler)
            => throw EndpointMapping.RequiresSourceGenerator();

        /// <summary>
        /// Maps a GET route, relative to the group prefix, to a typed handler. The Cohesion Web source
        /// generator intercepts this call.
        /// </summary>
        /// <param name="pattern">The route pattern, relative to the group prefix.</param>
        /// <param name="handler">The typed handler lambda whose parameters are bound from the request.</param>
        /// <returns>The mapped route's builder, for attaching endpoint metadata.</returns>
        /// <exception cref="NotSupportedException">Always thrown when the source generator did not rewrite the call site.</exception>
        public IRouterRouteBuilder MapGet(string pattern, Delegate handler)
            => throw EndpointMapping.RequiresSourceGenerator();

        /// <summary>
        /// Maps a POST route, relative to the group prefix, to a typed handler. The Cohesion Web source
        /// generator intercepts this call.
        /// </summary>
        /// <param name="pattern">The route pattern, relative to the group prefix.</param>
        /// <param name="handler">The typed handler lambda whose parameters are bound from the request.</param>
        /// <returns>The mapped route's builder, for attaching endpoint metadata.</returns>
        /// <exception cref="NotSupportedException">Always thrown when the source generator did not rewrite the call site.</exception>
        public IRouterRouteBuilder MapPost(string pattern, Delegate handler)
            => throw EndpointMapping.RequiresSourceGenerator();

        /// <summary>
        /// Maps a PUT route, relative to the group prefix, to a typed handler. The Cohesion Web source
        /// generator intercepts this call.
        /// </summary>
        /// <param name="pattern">The route pattern, relative to the group prefix.</param>
        /// <param name="handler">The typed handler lambda whose parameters are bound from the request.</param>
        /// <returns>The mapped route's builder, for attaching endpoint metadata.</returns>
        /// <exception cref="NotSupportedException">Always thrown when the source generator did not rewrite the call site.</exception>
        public IRouterRouteBuilder MapPut(string pattern, Delegate handler)
            => throw EndpointMapping.RequiresSourceGenerator();

        /// <summary>
        /// Maps a PATCH route, relative to the group prefix, to a typed handler. The Cohesion Web source
        /// generator intercepts this call.
        /// </summary>
        /// <param name="pattern">The route pattern, relative to the group prefix.</param>
        /// <param name="handler">The typed handler lambda whose parameters are bound from the request.</param>
        /// <returns>The mapped route's builder, for attaching endpoint metadata.</returns>
        /// <exception cref="NotSupportedException">Always thrown when the source generator did not rewrite the call site.</exception>
        public IRouterRouteBuilder MapPatch(string pattern, Delegate handler)
            => throw EndpointMapping.RequiresSourceGenerator();

        /// <summary>
        /// Maps a DELETE route, relative to the group prefix, to a typed handler. The Cohesion Web
        /// source generator intercepts this call.
        /// </summary>
        /// <param name="pattern">The route pattern, relative to the group prefix.</param>
        /// <param name="handler">The typed handler lambda whose parameters are bound from the request.</param>
        /// <returns>The mapped route's builder, for attaching endpoint metadata.</returns>
        /// <exception cref="NotSupportedException">Always thrown when the source generator did not rewrite the call site.</exception>
        public IRouterRouteBuilder MapDelete(string pattern, Delegate handler)
            => throw EndpointMapping.RequiresSourceGenerator();
    }
}

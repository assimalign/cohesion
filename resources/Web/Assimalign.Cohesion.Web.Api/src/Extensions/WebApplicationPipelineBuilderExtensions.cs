using System;

namespace Assimalign.Cohesion.Web;

using Assimalign.Cohesion.Http;
using Assimalign.Cohesion.Web.Internal;
using Assimalign.Cohesion.Web.Routing;

/// <summary>
/// API-oriented endpoint mapping helpers for web application pipelines.
/// </summary>
/// <remarks>
/// <para>
/// Two families of overloads live here:
/// </para>
/// <list type="bullet">
/// <item>
/// The <see cref="WebApplicationMiddleware"/> overloads register a terminal endpoint verbatim — no
/// parameter binding takes place.
/// </item>
/// <item>
/// The <see cref="Delegate"/> overloads accept a typed handler — a lambda such as
/// <c>(long id) =&gt; orders.Get(id)</c>, or a method group. They are placeholders: the Cohesion Web
/// source generator (<c>Assimalign.Cohesion.SourceGeneration.Web</c>) intercepts the call site and
/// substitutes an AOT-safe thunk that binds the handler's parameters, invokes it, and writes the value
/// it returns, if any: a <see cref="string"/> as <c>text/plain</c>, <see langword="null"/> as
/// <c>204 No Content</c>, and any other value through the content-serialization registry with
/// <c>Accept</c> negotiation. The generator also describes the endpoint on its route, with an
/// <see cref="EndpointParameterMetadata"/> per request-bound parameter and its
/// <see cref="EndpointResponseMetadata"/> responses, for documentation adapters. A handler the generator
/// cannot bind is a <c>COHWEB</c> compile-time error; the placeholder bodies throw only when the
/// generator was not wired in at all.
/// </item>
/// </list>
/// <para>
/// Every <c>Map*</c> returns the mapped route's <see cref="IRouterRouteBuilder"/>, so per-endpoint
/// policies attach where the endpoint is mapped (<c>WithMetadata</c>, <c>WithName</c>, and feature verbs
/// such as <c>RequireRateLimiting</c>). Route groups carry the same verbs
/// (<see cref="RouterGroupBuilderEndpointExtensions"/>).
/// </para>
/// </remarks>
public static class WebApplicationPipelineBuilderExtensions
{
    extension<TBuilder>(TBuilder builder) where TBuilder : IWebApplicationPipelineBuilder, IWebApplication
    {
        /// <summary>
        /// Creates a route group on the application's router: every endpoint mapped through the group
        /// composes <paramref name="prefix"/> and the group's shared metadata and parameter policies.
        /// </summary>
        /// <param name="prefix">
        /// The route-template prefix applied to the group's endpoints. May contain parameters (for
        /// example <c>{tenant}/api</c>) and may be empty to share only metadata and policies.
        /// </param>
        /// <returns>The route group builder.</returns>
        /// <exception cref="ArgumentNullException"><paramref name="builder"/> or <paramref name="prefix"/> is <see langword="null"/>.</exception>
        /// <exception cref="InvalidOperationException">Routing has not been registered (call <c>AddRouting</c>).</exception>
        /// <exception cref="Routing.Exceptions.RoutePatternException"><paramref name="prefix"/> is not a valid route template.</exception>
        public IRouterGroupBuilder MapGroup(string prefix)
        {
            ArgumentNullException.ThrowIfNull(builder);
            ArgumentNullException.ThrowIfNull(prefix);

            return EndpointMapping.GetRouterBuilder(builder.Context).MapGroup(prefix);
        }

        /// <summary>
        /// Maps a route to the supplied terminal middleware.
        /// </summary>
        /// <param name="method">The HTTP method the route matches.</param>
        /// <param name="pattern">The route pattern to parse.</param>
        /// <param name="middleware">The middleware to execute when the route matches.</param>
        /// <returns>The mapped route's builder, for attaching endpoint metadata.</returns>
        /// <exception cref="ArgumentNullException"><paramref name="builder"/> or <paramref name="middleware"/> is <see langword="null"/>.</exception>
        /// <exception cref="ArgumentException"><paramref name="pattern"/> is <see langword="null"/> or empty.</exception>
        /// <exception cref="InvalidOperationException">Routing has not been registered (call <c>AddRouting</c>), or the route table has already been built.</exception>
        public IRouterRouteBuilder Map(HttpMethod method, string pattern, WebApplicationMiddleware middleware)
        {
            ArgumentException.ThrowIfNullOrEmpty(pattern);
            ArgumentNullException.ThrowIfNull(builder);
            ArgumentNullException.ThrowIfNull(middleware);

            return EndpointMapping.GetRouterBuilder(builder.Context).Map(method, pattern, new RouterRouteHandler(middleware));
        }

        /// <summary>
        /// Maps the application's fallback route to the supplied terminal middleware: it answers
        /// <c>GET</c> (and <c>HEAD</c>) requests whose path no other route matches and whose last segment
        /// names no file (<c>{**path:nonfile}</c>). See <c>IRouterBuilder.MapFallback</c> for the semantics.
        /// </summary>
        /// <param name="middleware">The middleware to execute for requests the fallback answers.</param>
        /// <returns>The fallback route's builder, for attaching endpoint metadata.</returns>
        /// <exception cref="ArgumentNullException"><paramref name="builder"/> or <paramref name="middleware"/> is <see langword="null"/>.</exception>
        /// <exception cref="InvalidOperationException">Routing has not been registered (call <c>AddRouting</c>), or the route table has already been built.</exception>
        public IRouterRouteBuilder MapFallback(WebApplicationMiddleware middleware)
        {
            ArgumentNullException.ThrowIfNull(builder);
            ArgumentNullException.ThrowIfNull(middleware);

            return EndpointMapping.GetRouterBuilder(builder.Context).MapFallback(new RouterRouteHandler(middleware));
        }

        /// <summary>
        /// Maps a fallback route with its own template (for example <c>admin/{**path:nonfile}</c>) to the
        /// supplied terminal middleware.
        /// </summary>
        /// <param name="pattern">The fallback route's template.</param>
        /// <param name="middleware">The middleware to execute for requests the fallback answers.</param>
        /// <returns>The fallback route's builder, for attaching endpoint metadata.</returns>
        /// <exception cref="ArgumentNullException"><paramref name="builder"/> or <paramref name="middleware"/> is <see langword="null"/>.</exception>
        /// <exception cref="ArgumentException"><paramref name="pattern"/> is <see langword="null"/> or empty.</exception>
        /// <exception cref="InvalidOperationException">Routing has not been registered (call <c>AddRouting</c>), or the route table has already been built.</exception>
        public IRouterRouteBuilder MapFallback(string pattern, WebApplicationMiddleware middleware)
        {
            ArgumentNullException.ThrowIfNull(builder);
            ArgumentException.ThrowIfNullOrEmpty(pattern);
            ArgumentNullException.ThrowIfNull(middleware);

            return EndpointMapping.GetRouterBuilder(builder.Context).MapFallback(pattern, new RouterRouteHandler(middleware));
        }

        /// <summary>
        /// Maps a GET route pattern to the supplied terminal middleware.
        /// </summary>
        /// <param name="pattern">The route pattern to parse.</param>
        /// <param name="middleware">The middleware to execute when the route matches.</param>
        /// <returns>The mapped route's builder, for attaching endpoint metadata.</returns>
        /// <exception cref="ArgumentNullException"><paramref name="builder"/> or <paramref name="middleware"/> is <see langword="null"/>.</exception>
        /// <exception cref="ArgumentException"><paramref name="pattern"/> is <see langword="null"/> or empty.</exception>
        /// <exception cref="InvalidOperationException">Routing has not been registered (call <c>AddRouting</c>), or the route table has already been built.</exception>
        public IRouterRouteBuilder MapGet(
            string pattern,
            WebApplicationMiddleware middleware)
        {
            ArgumentNullException.ThrowIfNull(builder);
            ArgumentException.ThrowIfNullOrEmpty(pattern);
            ArgumentNullException.ThrowIfNull(middleware);

            return builder.Map(HttpMethod.Get, pattern, middleware);
        }

        /// <summary>
        /// Maps a route to a typed handler whose parameters are bound from the request. The Cohesion
        /// Web source generator intercepts this call and substitutes an AOT-safe binding thunk.
        /// </summary>
        /// <param name="method">The HTTP method the route matches.</param>
        /// <param name="pattern">The route pattern to parse.</param>
        /// <param name="handler">The typed handler, a lambda or method group: its parameters are bound from the request and the value it returns, if any, is written as the response.</param>
        /// <returns>The mapped route's builder, for attaching endpoint metadata.</returns>
        /// <exception cref="NotSupportedException">Always thrown when the source generator did not rewrite the call site.</exception>
        public IRouterRouteBuilder Map(HttpMethod method, string pattern, Delegate handler)
            => throw EndpointMapping.RequiresSourceGenerator();

        /// <summary>
        /// Maps a GET route to a typed handler whose parameters are bound from the request. The
        /// Cohesion Web source generator intercepts this call and substitutes an AOT-safe binding thunk.
        /// </summary>
        /// <param name="pattern">The route pattern to parse.</param>
        /// <param name="handler">The typed handler, a lambda or method group: its parameters are bound from the request and the value it returns, if any, is written as the response.</param>
        /// <returns>The mapped route's builder, for attaching endpoint metadata.</returns>
        /// <exception cref="NotSupportedException">Always thrown when the source generator did not rewrite the call site.</exception>
        public IRouterRouteBuilder MapGet(string pattern, Delegate handler)
            => throw EndpointMapping.RequiresSourceGenerator();

        /// <summary>
        /// Maps a POST route to a typed handler whose parameters are bound from the request. The
        /// Cohesion Web source generator intercepts this call and substitutes an AOT-safe binding thunk.
        /// </summary>
        /// <param name="pattern">The route pattern to parse.</param>
        /// <param name="handler">The typed handler, a lambda or method group: its parameters are bound from the request and the value it returns, if any, is written as the response.</param>
        /// <returns>The mapped route's builder, for attaching endpoint metadata.</returns>
        /// <exception cref="NotSupportedException">Always thrown when the source generator did not rewrite the call site.</exception>
        public IRouterRouteBuilder MapPost(string pattern, Delegate handler)
            => throw EndpointMapping.RequiresSourceGenerator();

        /// <summary>
        /// Maps a PUT route to a typed handler whose parameters are bound from the request. The
        /// Cohesion Web source generator intercepts this call and substitutes an AOT-safe binding thunk.
        /// </summary>
        /// <param name="pattern">The route pattern to parse.</param>
        /// <param name="handler">The typed handler, a lambda or method group: its parameters are bound from the request and the value it returns, if any, is written as the response.</param>
        /// <returns>The mapped route's builder, for attaching endpoint metadata.</returns>
        /// <exception cref="NotSupportedException">Always thrown when the source generator did not rewrite the call site.</exception>
        public IRouterRouteBuilder MapPut(string pattern, Delegate handler)
            => throw EndpointMapping.RequiresSourceGenerator();

        /// <summary>
        /// Maps a PATCH route to a typed handler whose parameters are bound from the request. The
        /// Cohesion Web source generator intercepts this call and substitutes an AOT-safe binding thunk.
        /// </summary>
        /// <param name="pattern">The route pattern to parse.</param>
        /// <param name="handler">The typed handler, a lambda or method group: its parameters are bound from the request and the value it returns, if any, is written as the response.</param>
        /// <returns>The mapped route's builder, for attaching endpoint metadata.</returns>
        /// <exception cref="NotSupportedException">Always thrown when the source generator did not rewrite the call site.</exception>
        public IRouterRouteBuilder MapPatch(string pattern, Delegate handler)
            => throw EndpointMapping.RequiresSourceGenerator();

        /// <summary>
        /// Maps a DELETE route to a typed handler whose parameters are bound from the request. The
        /// Cohesion Web source generator intercepts this call and substitutes an AOT-safe binding thunk.
        /// </summary>
        /// <param name="pattern">The route pattern to parse.</param>
        /// <param name="handler">The typed handler, a lambda or method group: its parameters are bound from the request and the value it returns, if any, is written as the response.</param>
        /// <returns>The mapped route's builder, for attaching endpoint metadata.</returns>
        /// <exception cref="NotSupportedException">Always thrown when the source generator did not rewrite the call site.</exception>
        public IRouterRouteBuilder MapDelete(string pattern, Delegate handler)
            => throw EndpointMapping.RequiresSourceGenerator();
    }
}

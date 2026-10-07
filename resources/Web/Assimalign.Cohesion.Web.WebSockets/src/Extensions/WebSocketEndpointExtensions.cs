using System;
using System.Linq;
using System.Net.WebSockets;
using System.Threading.Tasks;

using Assimalign.Cohesion.Http;
using Assimalign.Cohesion.Web.Routing;
using Assimalign.Cohesion.Web.WebSockets.Internal;

namespace Assimalign.Cohesion.Web.WebSockets;

/// <summary>
/// Maps WebSocket endpoints on an application and on route groups: <c>MapWebSocket</c> serves one
/// handler for every shape a WebSocket handshake takes.
/// </summary>
/// <remarks>
/// <para>
/// Over HTTP/1.1 a WebSocket handshake is a <c>GET</c> that asks to upgrade (RFC 6455). Over HTTP/2
/// and HTTP/3 it is an extended <c>CONNECT</c> (RFC 8441, RFC 9220). A handler mapped with
/// <c>MapGet</c> therefore answers only HTTP/1.1 clients, which is what a local test over
/// <c>http://localhost</c> uses, and fails every browser behind <c>UseHttps</c>, which negotiates
/// HTTP/2 through ALPN. <c>MapWebSocket</c> maps both methods on one route, so the same endpoint
/// serves every protocol.
/// </para>
/// <para>
/// The endpoint answers a request that is not a WebSocket handshake (a plain <c>GET</c> or
/// <c>HEAD</c>, or a <c>CONNECT</c> without <c>:protocol</c>) with <c>400 Bad Request</c> and never
/// runs the handler; any other method gets the router's <c>405</c>. A valid handshake is accepted
/// and the handler receives the open socket. The socket is disposed when the handler returns, so
/// complete the close handshake in the handler.
/// </para>
/// <para>
/// The WebSocket policy guards every handshake the endpoint accepts: the one <c>UseWebSockets</c>
/// configured when it is registered, otherwise the default policy, which refuses a cross-site
/// handshake with <c>403</c>. The returned route builder takes the route's policies as for any
/// endpoint (<c>RequireAuthorization</c>, <c>RequireCors</c>, <c>RequireRateLimiting</c>,
/// <c>WithName</c>). A socket outlives any request timeout, so an application with a request-timeout
/// policy disables it on the endpoint (<c>DisableRequestTimeout()</c>).
/// </para>
/// <code>
/// app.MapWebSocket("/chat/{room}", async (IHttpContext context, WebSocket socket) =&gt;
/// {
///     // receive and send until the peer closes, then complete the close handshake
/// });
/// </code>
/// </remarks>
public static class WebSocketEndpointExtensions
{
    extension<TBuilder>(TBuilder builder) where TBuilder : IWebApplicationPipelineBuilder, IWebApplication
    {
        /// <summary>
        /// Maps a WebSocket endpoint: one route that accepts the HTTP/1.1 upgrade (<c>GET</c>) and the
        /// HTTP/2 and HTTP/3 extended CONNECT, and runs <paramref name="handler"/> over the accepted
        /// socket.
        /// </summary>
        /// <param name="pattern">The route pattern.</param>
        /// <param name="handler">The handler that drives the accepted socket; the socket is disposed when it returns.</param>
        /// <returns>The mapped route's builder, for attaching endpoint metadata and policies.</returns>
        /// <exception cref="ArgumentNullException"><paramref name="builder"/> or <paramref name="handler"/> is <see langword="null"/>.</exception>
        /// <exception cref="ArgumentException"><paramref name="pattern"/> is <see langword="null"/> or empty.</exception>
        /// <exception cref="InvalidOperationException">Routing has not been registered (call <c>AddRouting</c>), or the route table has already been built.</exception>
        public IRouterRouteBuilder MapWebSocket(string pattern, Func<IHttpContext, WebSocket, Task> handler)
            => builder.MapWebSocket(pattern, handler, acceptOptions: null);

        /// <summary>
        /// Maps a WebSocket endpoint whose accept options are chosen per handshake, for example the
        /// subprotocol among the ones the client offered (<c>context.WebSockets.RequestedProtocols</c>)
        /// or compression.
        /// </summary>
        /// <param name="pattern">The route pattern.</param>
        /// <param name="handler">The handler that drives the accepted socket; the socket is disposed when it returns.</param>
        /// <param name="acceptOptions">
        /// Chooses the options of each accept after the handshake was validated, or <see langword="null"/>
        /// for the policy's defaults. A value it leaves <see langword="null"/> takes the policy's default.
        /// </param>
        /// <returns>The mapped route's builder, for attaching endpoint metadata and policies.</returns>
        /// <exception cref="ArgumentNullException"><paramref name="builder"/> or <paramref name="handler"/> is <see langword="null"/>.</exception>
        /// <exception cref="ArgumentException"><paramref name="pattern"/> is <see langword="null"/> or empty.</exception>
        /// <exception cref="InvalidOperationException">Routing has not been registered (call <c>AddRouting</c>), or the route table has already been built.</exception>
        public IRouterRouteBuilder MapWebSocket(
            string pattern,
            Func<IHttpContext, WebSocket, Task> handler,
            Func<IHttpContext, HttpWebSocketAcceptOptions?>? acceptOptions)
        {
            ArgumentNullException.ThrowIfNull(builder);
            ArgumentException.ThrowIfNullOrEmpty(pattern);
            ArgumentNullException.ThrowIfNull(handler);

            IRouterFeature routing = builder.Context.Features.OfType<IRouterFeature>().FirstOrDefault()
                ?? throw new InvalidOperationException(
                    "No router builder was registered. Call AddRouting() on the application builder before mapping endpoints.");

            return routing.Builder.Map(WebSocketEndpoint.Methods, pattern, CreateHandler(handler, acceptOptions));
        }
    }

    extension(IRouterGroupBuilder group)
    {
        /// <summary>
        /// Maps a WebSocket endpoint, relative to the group prefix: one route that accepts the
        /// HTTP/1.1 upgrade (<c>GET</c>) and the HTTP/2 and HTTP/3 extended CONNECT, and runs
        /// <paramref name="handler"/> over the accepted socket. The group's metadata, such as an
        /// authorization policy, applies to it.
        /// </summary>
        /// <param name="pattern">The route pattern, relative to the group prefix. An empty string maps the prefix itself.</param>
        /// <param name="handler">The handler that drives the accepted socket; the socket is disposed when it returns.</param>
        /// <returns>The mapped route's builder, for attaching endpoint metadata and policies.</returns>
        /// <exception cref="ArgumentNullException"><paramref name="group"/>, <paramref name="pattern"/> or <paramref name="handler"/> is <see langword="null"/>.</exception>
        /// <exception cref="Routing.Exceptions.RoutePatternException">The composed template is not a valid route template.</exception>
        public IRouterRouteBuilder MapWebSocket(string pattern, Func<IHttpContext, WebSocket, Task> handler)
            => group.MapWebSocket(pattern, handler, acceptOptions: null);

        /// <summary>
        /// Maps a WebSocket endpoint, relative to the group prefix, whose accept options are chosen per
        /// handshake.
        /// </summary>
        /// <param name="pattern">The route pattern, relative to the group prefix. An empty string maps the prefix itself.</param>
        /// <param name="handler">The handler that drives the accepted socket; the socket is disposed when it returns.</param>
        /// <param name="acceptOptions">
        /// Chooses the options of each accept after the handshake was validated, or <see langword="null"/>
        /// for the policy's defaults. A value it leaves <see langword="null"/> takes the policy's default.
        /// </param>
        /// <returns>The mapped route's builder, for attaching endpoint metadata and policies.</returns>
        /// <exception cref="ArgumentNullException"><paramref name="group"/>, <paramref name="pattern"/> or <paramref name="handler"/> is <see langword="null"/>.</exception>
        /// <exception cref="Routing.Exceptions.RoutePatternException">The composed template is not a valid route template.</exception>
        public IRouterRouteBuilder MapWebSocket(
            string pattern,
            Func<IHttpContext, WebSocket, Task> handler,
            Func<IHttpContext, HttpWebSocketAcceptOptions?>? acceptOptions)
        {
            ArgumentNullException.ThrowIfNull(group);
            ArgumentNullException.ThrowIfNull(pattern);
            ArgumentNullException.ThrowIfNull(handler);

            return group.Map(WebSocketEndpoint.Methods, pattern, CreateHandler(handler, acceptOptions));
        }
    }

    private static RouterRouteHandler CreateHandler(
        Func<IHttpContext, WebSocket, Task> handler,
        Func<IHttpContext, HttpWebSocketAcceptOptions?>? acceptOptions)
    {
        WebSocketEndpoint endpoint = new(handler, acceptOptions);
        return new RouterRouteHandler(endpoint.InvokeAsync);
    }
}

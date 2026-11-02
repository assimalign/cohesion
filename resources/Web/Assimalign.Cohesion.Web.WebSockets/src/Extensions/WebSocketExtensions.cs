using System;
using System.Linq;

using Assimalign.Cohesion.Web.WebSockets.Internal;

namespace Assimalign.Cohesion.Web.WebSockets;

/// <summary>
/// Pipeline-builder members that place the WebSocket policy in a web application.
/// </summary>
/// <remarks>
/// <para>
/// The handshake and the framing are <c>Assimalign.Cohesion.Http.WebSockets</c>'s: an endpoint
/// mapped with <c>MapWebSocket</c> (<see cref="WebSocketEndpointExtensions"/>), or one that reads
/// <c>context.WebSockets</c>, accepts the socket. <c>UseWebSockets</c> places the policy an
/// application that serves browsers needs around that accept, and every accept downstream of it
/// takes the policy whether or not it was written for it. The policy is configured where it is
/// registered, with <c>builder.Services.AddWebSockets(webSockets =&gt; ...)</c>
/// (<see cref="WebSocketsBuilder"/>).
/// </para>
/// <para>
/// Place it after <c>UseForwardedHeaders</c>, whose effective scheme and host the origin check
/// reads, and ahead of every endpoint that accepts a socket. An endpoint that accepts a socket
/// under a request-timeout policy is cancelled when the timeout fires, so disable the timeout on
/// it (<c>DisableRequestTimeout()</c> in <c>Assimalign.Cohesion.Web.RequestTimeouts</c>).
/// </para>
/// </remarks>
public static class WebSocketExtensions
{
    extension(IWebApplicationPipelineBuilder builder)
    {
        /// <summary>
        /// Places the WebSocket policy middleware the application registered with
        /// <c>builder.Services.AddWebSockets(webSockets =&gt; ...)</c>, or one with the default policy
        /// when it registered none.
        /// </summary>
        /// <remarks>
        /// <para>For a WebSocket opening handshake, the middleware:</para>
        /// <list type="bullet">
        /// <item><description>refuses a malformed handshake with <c>400 Bad Request</c>, and one
        /// for a version other than 13 with <c>426 Upgrade Required</c> and
        /// <c>Sec-WebSocket-Version: 13</c> (RFC 6455 §4.2);</description></item>
        /// <item><description>refuses, with <c>403 Forbidden</c>, a handshake whose <c>Origin</c>
        /// is neither the request's own origin nor one of
        /// <see cref="WebSocketOptions.AllowedOrigins"/>, unless
        /// <see cref="WebSocketOptions.AllowAnyOrigin"/> is set. A handshake without
        /// <c>Origin</c> passes;</description></item>
        /// <item><description>gives every accept downstream the keep-alive and compression defaults
        /// of <see cref="WebSocketOptions"/>; compression stays off unless enabled;</description></item>
        /// <item><description>closes the accepted socket with <c>1001 Going Away</c> when the
        /// server begins its drain, so it ends cleanly within the stop's budget. This needs the
        /// server's drain signal (<see cref="IWebServerDrainFeature"/>), which the default server
        /// publishes and a custom server may not.</description></item>
        /// </list>
        /// <para>Any other request passes through untouched.</para>
        /// <para>
        /// The allowed origins and the accept defaults are configured where the policy is registered
        /// (<see cref="WebSocketsBuilder"/>); this verb only positions it. The default policy allows
        /// only same-origin handshakes, keeps sockets alive every 30 seconds, and leaves compression
        /// off. When the application registered the policy more than once, the last registration wins.
        /// </para>
        /// </remarks>
        /// <returns>The same pipeline builder, for chaining.</returns>
        /// <exception cref="ArgumentNullException"><paramref name="builder"/> is <see langword="null"/>.</exception>
        public IWebApplicationPipelineBuilder UseWebSockets()
        {
            ArgumentNullException.ThrowIfNull(builder);

            return builder.Use(static (context, next) =>
            {
                IWebApplicationMiddleware middleware = context.Middleware.OfType<WebSocketMiddleware>().LastOrDefault()
                    ?? new WebSocketsBuilder().Build();

                return exchange => middleware.InvokeAsync(exchange, next);
            });
        }
    }
}

using System;

using Assimalign.Cohesion.Web.WebSockets.Internal;

namespace Assimalign.Cohesion.Web.WebSockets;

/// <summary>
/// Pipeline-builder members that add the WebSocket policy to a web application.
/// </summary>
/// <remarks>
/// <para>
/// The handshake and the framing are <c>Assimalign.Cohesion.Http.WebSockets</c>'s: an endpoint
/// mapped with <c>MapWebSocket</c> (<see cref="WebSocketEndpointExtensions"/>), or one that reads
/// <c>context.WebSockets</c>, accepts the socket. <c>UseWebSockets</c> adds the policy an
/// application that serves browsers needs around that accept, and every accept downstream of it
/// takes the policy whether or not it was written for it.
/// </para>
/// <para>
/// Register it after <c>UseForwardedHeaders</c>, whose effective scheme and host the origin check
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
        /// Adds the WebSocket policy middleware to the pipeline.
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
        /// </remarks>
        /// <param name="configure">An optional callback that configures the allowed origins and the accept defaults.</param>
        /// <returns>The same pipeline builder, for chaining.</returns>
        /// <exception cref="ArgumentNullException"><paramref name="builder"/> is <see langword="null"/>.</exception>
        /// <exception cref="ArgumentException"><paramref name="configure"/> adds an allowed origin that is not a serialized origin.</exception>
        /// <exception cref="ArgumentOutOfRangeException"><paramref name="configure"/> sets a negative keep-alive interval or timeout.</exception>
        public IWebApplicationPipelineBuilder UseWebSockets(Action<WebSocketOptions>? configure = null)
        {
            ArgumentNullException.ThrowIfNull(builder);

            WebSocketOptions options = new();
            configure?.Invoke(options);

            return builder.Use(new WebSocketMiddleware(WebSocketPolicy.Create(options)));
        }
    }
}

using System;

using Assimalign.Cohesion.Web;
using Assimalign.Cohesion.Web.WebSockets.Internal;

namespace Assimalign.Cohesion.Web.WebSockets;

/// <summary>
/// Configures the WebSocket policy middleware that
/// <c>builder.Services.AddWebSockets(webSockets =&gt; ...)</c> registers and
/// <see cref="WebSocketExtensions.UseWebSockets(IWebApplicationPipelineBuilder)"/> places: which
/// origins may open a socket, and the defaults every accept takes.
/// </summary>
/// <remarks>
/// <see cref="Build"/> validates the allowed origins and takes a snapshot of the options, so a
/// malformed origin fails where the application registers the policy, never per handshake, and
/// changing the options afterwards has no effect on the middleware it returned. The keep-alive
/// values validate themselves when they are assigned.
/// </remarks>
public sealed class WebSocketsBuilder
{
    /// <summary>
    /// Initializes a builder with the default policy: only same-origin handshakes (and handshakes
    /// without <c>Origin</c>) pass, keep-alive every 30 seconds without a pong timeout, and
    /// compression off.
    /// </summary>
    public WebSocketsBuilder()
    {
    }

    /// <summary>
    /// Gets the policy's options: the allowed origins, whether any origin is allowed, and the
    /// keep-alive and compression defaults every accept takes.
    /// </summary>
    public WebSocketOptions Options { get; } = new();

    /// <summary>
    /// Builds the WebSocket policy middleware from <see cref="Options"/>.
    /// </summary>
    /// <returns>
    /// The middleware, which refuses a malformed handshake (<c>400</c>, or <c>426</c> for another
    /// version) and a cross-site one (<c>403</c>), gives every accept downstream the policy's defaults,
    /// and closes open sockets with <c>1001 Going Away</c> when the server drains.
    /// </returns>
    /// <exception cref="ArgumentException">
    /// An entry of <see cref="WebSocketOptions.AllowedOrigins"/> is empty, the wildcard <c>*</c>, the
    /// opaque origin <c>null</c>, or not a serialized origin.
    /// </exception>
    public IWebApplicationMiddleware Build()
    {
        return new WebSocketMiddleware(WebSocketPolicy.Create(Options));
    }
}

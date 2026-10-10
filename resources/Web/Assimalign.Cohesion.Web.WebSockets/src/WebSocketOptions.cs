using System;
using System.Collections.Generic;
using System.Net.WebSockets;
using System.Threading;

using Assimalign.Cohesion.Http;

namespace Assimalign.Cohesion.Web.WebSockets;

/// <summary>
/// Builder-time options for the WebSocket policy middleware
/// (<see cref="WebSocketExtensions.UseWebSockets(IWebApplicationPipelineBuilder, Action{WebSocketOptions})"/>):
/// which origins may open a socket, and the defaults every accept takes.
/// </summary>
/// <remarks>
/// <para>
/// Composition is dependency-free: <c>UseWebSockets</c> validates the options and keeps its own
/// copy, so changing them afterwards has no effect. No service container, configuration binding or
/// request-time service location is involved.
/// </para>
/// <para>
/// An accept overrides any default through <see cref="HttpWebSocketAcceptOptions"/>: a value set
/// there wins, and a <see langword="null"/> one takes the default configured here.
/// </para>
/// </remarks>
public sealed class WebSocketOptions
{
    private TimeSpan _keepAliveInterval = WebSocket.DefaultKeepAliveInterval;
    private TimeSpan _keepAliveTimeout = Timeout.InfiniteTimeSpan;

    /// <summary>
    /// Gets the origins, besides the request's own, whose pages may open a WebSocket. Each is a
    /// serialized origin, <c>scheme://host[:port]</c>, as a browser sends it in <c>Origin</c>, for
    /// example <c>https://app.example</c>. Case and a default port are normalized; a path, a
    /// wildcard and the opaque origin <c>null</c> are rejected when <c>UseWebSockets</c> runs.
    /// </summary>
    /// <remarks>
    /// <para>
    /// A browser sends cookies with a WebSocket handshake to any site, and the same-origin policy
    /// does not apply to WebSockets (CORS neither). Without an origin check, any page a user visits
    /// could open a socket authenticated as that user: cross-site WebSocket hijacking. So a
    /// handshake whose <c>Origin</c> is neither the request's own origin nor listed here is refused
    /// with <c>403 Forbidden</c>. Browsers always send <c>Origin</c> on a WebSocket handshake, so a
    /// handshake without one does not come from a page, cannot be a hijack, and is allowed.
    /// </para>
    /// <para>
    /// The request's own origin is built from the effective scheme and host
    /// (<c>context.EffectiveScheme</c>, <c>context.EffectiveHost</c>). Behind a proxy that
    /// terminates TLS, register <c>UseForwardedHeaders</c> before <c>UseWebSockets</c>, or list
    /// the public origin here; otherwise a same-site page reads as cross-site.
    /// </para>
    /// </remarks>
    public ICollection<string> AllowedOrigins { get; } = new List<string>();

    /// <summary>
    /// Gets or sets whether a handshake from any origin is allowed, which turns the
    /// cross-site WebSocket hijacking defense off. The default is <see langword="false"/>.
    /// </summary>
    /// <remarks>
    /// Turn it on only when the sockets carry no ambient credentials: they authenticate with a token
    /// sent in a message or in the handshake's query, never with a cookie or HTTP authentication
    /// the browser adds by itself.
    /// </remarks>
    public bool AllowAnyOrigin { get; set; }

    /// <summary>
    /// Gets or sets how often an accepted socket sends a keep-alive frame while it is idle, unless
    /// the accept sets its own. <see cref="TimeSpan.Zero"/> or <see cref="Timeout.InfiniteTimeSpan"/>
    /// disables keep-alive. The default is <see cref="WebSocket.DefaultKeepAliveInterval"/>
    /// (30 seconds), under the 60-second idle timeout common to proxies and load balancers.
    /// </summary>
    /// <exception cref="ArgumentOutOfRangeException">
    /// The value is negative and is not <see cref="Timeout.InfiniteTimeSpan"/>.
    /// </exception>
    public TimeSpan KeepAliveInterval
    {
        get => _keepAliveInterval;
        set => _keepAliveInterval = Validate(value, nameof(KeepAliveInterval));
    }

    /// <summary>
    /// Gets or sets how long an accepted socket waits for the <c>Pong</c> that answers its
    /// keep-alive <c>Ping</c> before it aborts, unless the accept sets its own.
    /// <see cref="TimeSpan.Zero"/> or <see cref="Timeout.InfiniteTimeSpan"/> (the default) sends
    /// unsolicited <c>Pong</c> frames instead and never times out.
    /// </summary>
    /// <exception cref="ArgumentOutOfRangeException">
    /// The value is negative and is not <see cref="Timeout.InfiniteTimeSpan"/>.
    /// </exception>
    public TimeSpan KeepAliveTimeout
    {
        get => _keepAliveTimeout;
        set => _keepAliveTimeout = Validate(value, nameof(KeepAliveTimeout));
    }

    /// <summary>
    /// Gets or sets whether an accept negotiates permessage-deflate compression (RFC 7692) when the
    /// client offers it, unless the accept decides for itself. The default is
    /// <see langword="false"/>.
    /// </summary>
    /// <remarks>
    /// Compression opens side channels: when a message compresses data an attacker controls
    /// together with a secret, the compressed size leaks the secret (the CRIME and BREACH attacks).
    /// Enabling it for every socket is safe only when no socket's messages mix the two; otherwise
    /// enable it per accept (<see cref="HttpWebSocketAcceptOptions.DangerousEnableCompression"/>).
    /// </remarks>
    public bool DangerousEnableCompression { get; set; }

    private static TimeSpan Validate(TimeSpan value, string propertyName)
    {
        if (value < TimeSpan.Zero && value != Timeout.InfiniteTimeSpan)
        {
            throw new ArgumentOutOfRangeException(
                propertyName,
                value,
                "The interval must be zero or positive, or Timeout.InfiniteTimeSpan.");
        }

        return value;
    }
}

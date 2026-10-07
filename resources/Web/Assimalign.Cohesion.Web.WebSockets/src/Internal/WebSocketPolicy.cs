using System;
using System.Collections.Generic;

using Assimalign.Cohesion.Http;

namespace Assimalign.Cohesion.Web.WebSockets.Internal;

/// <summary>
/// The validated, immutable snapshot of <see cref="WebSocketOptions"/> the middleware enforces: the
/// origin check, and the defaults applied to every accept.
/// </summary>
internal sealed class WebSocketPolicy
{
    private readonly HashSet<string> _allowedOrigins;
    private readonly bool _allowAnyOrigin;
    private readonly TimeSpan _keepAliveInterval;
    private readonly TimeSpan _keepAliveTimeout;
    private readonly bool _enableCompression;

    private WebSocketPolicy(WebSocketOptions options, HashSet<string> allowedOrigins)
    {
        _allowedOrigins = allowedOrigins;
        _allowAnyOrigin = options.AllowAnyOrigin;
        _keepAliveInterval = options.KeepAliveInterval;
        _keepAliveTimeout = options.KeepAliveTimeout;
        _enableCompression = options.DangerousEnableCompression;
    }

    /// <summary>
    /// Validates <paramref name="options"/> and takes its snapshot.
    /// </summary>
    /// <param name="options">The configured options.</param>
    /// <returns>The policy.</returns>
    /// <exception cref="ArgumentException">An allowed origin is not a serialized origin.</exception>
    public static WebSocketPolicy Create(WebSocketOptions options)
    {
        HashSet<string> allowedOrigins = new(StringComparer.Ordinal);

        foreach (string origin in options.AllowedOrigins)
        {
            allowedOrigins.Add(WebSocketOrigin.NormalizeConfigured(origin));
        }

        return new WebSocketPolicy(options, allowedOrigins);
    }

    /// <summary>
    /// Gets whether the handshake may open a socket: it has no <c>Origin</c>, any origin is allowed,
    /// or its <c>Origin</c> is the request's own origin or an allowed one.
    /// </summary>
    /// <param name="context">The exchange carrying the handshake.</param>
    /// <returns><see langword="false"/> when the handshake must be refused with <c>403</c>.</returns>
    public bool IsOriginAllowed(IHttpContext context)
    {
        if (_allowAnyOrigin
            || !context.Request.Headers.TryGetValue(HttpHeaderKey.Origin, out HttpHeaderValue value)
            || HttpHeaderValue.IsNullOrEmpty(value))
        {
            return true;
        }

        // A browser sends exactly one Origin; several are not a browser's handshake to trust.
        if (value.Count != 1 || value[0] is not string origin)
        {
            return false;
        }

        // The opaque origin "null" (a sandboxed page, a local file) and anything malformed fail to
        // parse, so they are refused.
        if (!WebSocketOrigin.TryNormalize(origin.Trim(), out string? serialized))
        {
            return false;
        }

        return _allowedOrigins.Contains(serialized)
            || (WebSocketOrigin.TryGetRequestOrigin(context, out string? own) && string.Equals(own, serialized, StringComparison.Ordinal));
    }

    /// <summary>
    /// Applies the policy's defaults to an accept: every setting the accept leaves
    /// <see langword="null"/> takes the policy's value. The caller's options are not modified.
    /// </summary>
    /// <param name="options">The accept's options, or <see langword="null"/>.</param>
    /// <returns>The options to accept with.</returns>
    public HttpWebSocketAcceptOptions Apply(HttpWebSocketAcceptOptions? options)
    {
        HttpWebSocketAcceptOptions effective = new()
        {
            SubProtocol = options?.SubProtocol,
            KeepAliveInterval = options?.KeepAliveInterval ?? _keepAliveInterval,
            KeepAliveTimeout = options?.KeepAliveTimeout ?? _keepAliveTimeout,
            DangerousEnableCompression = options?.DangerousEnableCompression ?? _enableCompression,
        };

        if (options is not null)
        {
            effective.DisableServerContextTakeover = options.DisableServerContextTakeover;
            effective.ServerMaxWindowBits = options.ServerMaxWindowBits;
        }

        return effective;
    }
}

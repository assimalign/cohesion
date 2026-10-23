using System;
using System.Globalization;
using System.Collections.Generic;
using System.IO;
using System.Net.WebSockets;
using System.Threading;
using System.Threading.Tasks;

namespace Assimalign.Cohesion.Http.Internal;

/// <summary>
/// The default <see cref="IHttpWebSocketFeature"/>: validates the opening handshake once, when the
/// exchange first reads <c>context.WebSockets</c>, and accepts it through the protocol's
/// <see cref="HttpWebSocketBootstrap"/>.
/// </summary>
/// <remarks>
/// A request that does not ask for a WebSocket gets the shared <see cref="NotWebSocket"/> instance,
/// which holds no exchange state and is never installed. A handshake attempt gets its own instance,
/// installed in the exchange's features so every later read, a decorator's included, sees the same
/// single-shot accept.
/// </remarks>
[global::System.Diagnostics.DebuggerDisplay("HttpWebSocketFeature {Name,nq}")]
[global::System.Diagnostics.DebuggerTypeProxy(typeof(DebugView))]
internal sealed class HttpWebSocketFeature : IHttpWebSocketFeature
{
    /// <summary>The name under which the feature is installed.</summary>
    public const string FeatureName = "Assimalign.Cohesion.Http.WebSockets";

    /// <summary>Gets the answer for every request that does not ask for a WebSocket.</summary>
    public static HttpWebSocketFeature NotWebSocket { get; } = new();

    private readonly IHttpContext? _context;
    private readonly HttpWebSocketBootstrap? _bootstrap;
    private int _accepted;

    private HttpWebSocketFeature()
    {
        HandshakeStatus = HttpWebSocketHandshakeStatus.None;
        RequestedProtocols = Array.Empty<string>();
    }

    private HttpWebSocketFeature(IHttpContext context, HttpWebSocketBootstrap bootstrap)
    {
        _context = context;
        _bootstrap = bootstrap;

        HttpWebSocketHandshakeStatus status = bootstrap.Validate();

        // The subprotocol list is read even for a refused handshake, so it can be inspected; a
        // malformed list makes an otherwise valid handshake invalid (RFC 6455 §4.2.1).
        if (!HttpWebSocketHandshake.TryGetRequestedProtocols(context.Request.Headers, out IReadOnlyList<string> protocols)
            && status == HttpWebSocketHandshakeStatus.Valid)
        {
            status = HttpWebSocketHandshakeStatus.Invalid;
        }

        HandshakeStatus = status;
        RequestedProtocols = protocols;
    }

    /// <inheritdoc />
    public string Name => FeatureName;

    /// <inheritdoc />
    public HttpWebSocketHandshakeStatus HandshakeStatus { get; }

    /// <inheritdoc />
    public bool IsWebSocketRequest => HandshakeStatus == HttpWebSocketHandshakeStatus.Valid;

    /// <inheritdoc />
    public IReadOnlyList<string> RequestedProtocols { get; }

    /// <summary>
    /// Returns the feature for <paramref name="context"/>: <see cref="NotWebSocket"/> when the request
    /// does not ask for a WebSocket, otherwise a new feature, installed in the exchange's features.
    /// </summary>
    /// <param name="context">The exchange.</param>
    /// <returns>The exchange's WebSocket feature.</returns>
    public static IHttpWebSocketFeature Create(IHttpContext context)
    {
        HttpWebSocketBootstrap? bootstrap = HttpWebSocketBootstrap.Select(context);

        if (bootstrap is null)
        {
            return NotWebSocket;
        }

        HttpWebSocketFeature feature = new(context, bootstrap);
        context.Features.Set(feature);

        return feature;
    }

    /// <inheritdoc />
    public async ValueTask<WebSocket> AcceptWebSocketAsync(
        HttpWebSocketAcceptOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        if (HandshakeStatus != HttpWebSocketHandshakeStatus.Valid || _context is null || _bootstrap is null)
        {
            throw new InvalidOperationException(
                string.Format(CultureInfo.CurrentCulture, ErrorMessages.HandshakeInvalid, HandshakeStatus));
        }

        string? subProtocol = options?.SubProtocol;

        // RFC 6455 §4.1: a client fails the connection when the server selects a subprotocol it did
        // not offer, so the mistake is reported here instead.
        if (subProtocol is not null && !IsRequested(subProtocol))
        {
            throw new ArgumentException(
                string.Format(CultureInfo.CurrentCulture, ErrorMessages.SubprotocolNotOffered, subProtocol),
                nameof(options));
        }

        if (Interlocked.Exchange(ref _accepted, 1) == 1)
        {
            throw new InvalidOperationException(ErrorMessages.HandshakeAlreadyAccepted);
        }

        WebSocketDeflateOptions? deflateOptions = null;
        string? extensions = null;

        if (options?.DangerousEnableCompression == true
            && _context.Request.Headers.TryGetValue(HttpHeaderKey.SecWebSocketExtensions, out HttpHeaderValue offers))
        {
            HttpWebSocketCompression.TryNegotiate(
                offers,
                options.DisableServerContextTakeover,
                options.ServerMaxWindowBits,
                out deflateOptions,
                out extensions);
        }

        // The handshake's own fields are set, or cleared, here, so the response always matches the
        // socket: a stale value the application staged can never contradict what was negotiated.
        IHttpHeaderCollection responseHeaders = _context.Response.Headers;
        SetOrRemove(responseHeaders, HttpHeaderKey.SecWebSocketProtocol, subProtocol);
        SetOrRemove(responseHeaders, HttpHeaderKey.SecWebSocketExtensions, extensions);
        _bootstrap.ApplyAcceptFields(responseHeaders);

        Stream stream = await _bootstrap.AcceptTransportAsync(cancellationToken).ConfigureAwait(false);

        return WebSocket.CreateFromStream(stream, new WebSocketCreationOptions
        {
            IsServer = true,
            SubProtocol = subProtocol,
            KeepAliveInterval = options?.KeepAliveInterval ?? WebSocket.DefaultKeepAliveInterval,
            KeepAliveTimeout = options?.KeepAliveTimeout ?? Timeout.InfiniteTimeSpan,
            DangerousDeflateOptions = deflateOptions,
        });
    }

    /// <inheritdoc />
    public void RejectHandshake()
    {
        if (_context is null || _bootstrap is null
            || HandshakeStatus is not (HttpWebSocketHandshakeStatus.Invalid or HttpWebSocketHandshakeStatus.UnsupportedVersion))
        {
            throw new InvalidOperationException(
                string.Format(CultureInfo.CurrentCulture, ErrorMessages.HandshakeRejectionNotSupported, HandshakeStatus));
        }

        IHttpResponse response = _context.Response;

        if (HandshakeStatus == HttpWebSocketHandshakeStatus.Invalid)
        {
            // RFC 6455 §4.2.1: a handshake that does not match the description is answered 400.
            response.StatusCode = HttpStatusCode.BadRequest;
            return;
        }

        // §4.2.2 item 4, §4.4: 426 with the version the server speaks, so the client can retry.
        response.StatusCode = HttpStatusCode.UpgradeRequired;
        response.Headers[HttpHeaderKey.SecWebSocketVersion] = HttpWebSocketHandshake.SupportedVersion;
        _bootstrap.ApplyUpgradeRequiredFields(response.Headers);
    }

    private bool IsRequested(string subProtocol)
    {
        foreach (string requested in RequestedProtocols)
        {
            if (string.Equals(requested, subProtocol, StringComparison.Ordinal))
            {
                return true;
            }
        }

        return false;
    }

    private static void SetOrRemove(IHttpHeaderCollection headers, HttpHeaderKey key, string? value)
    {
        if (value is null)
        {
            headers.Remove(key);
        }
        else
        {
            headers[key] = value;
        }
    }

    private sealed class DebugView
    {
        private readonly HttpWebSocketFeature _target;

        public DebugView(HttpWebSocketFeature value)
        {
            _target = value;
        }

        public string Name => _target.Name;
        public HttpWebSocketHandshakeStatus HandshakeStatus => _target.HandshakeStatus;
        public bool IsWebSocketRequest => _target.IsWebSocketRequest;
        public IReadOnlyList<string> RequestedProtocols => _target.RequestedProtocols;
    }
}

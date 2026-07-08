using System;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace Assimalign.Cohesion.Http;

/// <summary>
/// HTTP/1.1 implementation of <see cref="IHttpProtocolUpgrade"/>. Owns the response transition
/// for both <c>Connection: upgrade</c> + <c>Upgrade</c> (101 Switching Protocols) and
/// <c>CONNECT</c> tunnels (200 OK), built over the transport's generic
/// <see cref="IHttpConnectionTakeover"/> capability.
/// </summary>
/// <remarks>
/// <para>
/// Constructed by <see cref="HttpProtocolUpgradeInterceptor"/>'s response hook from
/// interceptor-seam materials only: the takeover capability, the exchange's live response header
/// collection, and its feature collection (for response cookies). <see cref="AcceptAsync"/> may
/// be invoked at most once per exchange — a second call surfaces as
/// <see cref="InvalidOperationException"/> before any byte is written, so it can never produce a
/// second response on the wire.
/// </para>
/// <para>
/// Acceptance claims the connection first (<see cref="IHttpConnectionTakeover.TakeOver"/> — from
/// that point the transport suppresses its own response and ends keep-alive), then writes the
/// status line and the connection-specific response headers (<c>Connection: Upgrade</c> +
/// <c>Upgrade: &lt;protocol&gt;</c> for an upgrade) directly to the surrendered raw stream.
/// <c>Content-Length</c> / <c>Transfer-Encoding</c> are scrubbed unconditionally — RFC 9112 §9.9
/// (a 101 has no body framing) and RFC 9110 §9.3.6 (a successful CONNECT response must not
/// include them) — so the tunnel never starts with stale framing metadata. Any other response
/// headers and cookies the application set before accepting are emitted with the transition
/// response (for example <c>Sec-WebSocket-Accept</c> on a WebSocket handshake).
/// </para>
/// </remarks>
internal sealed class Http1ProtocolUpgrade : IHttpProtocolUpgrade
{
    private static readonly HttpHeaderKey[] ForbiddenResponseHeaders =
    {
        HttpHeaderKey.ContentLength,
        HttpHeaderKey.TransferEncoding,
    };

    private readonly IHttpConnectionTakeover _takeover;
    private readonly HttpHeaderCollection _responseHeaders;
    private readonly IHttpFeatureCollection _features;
    private int _accepted;

    /// <summary>
    /// Initializes the upgrade for the current exchange.
    /// </summary>
    /// <param name="takeover">The transport's connection-takeover capability.</param>
    /// <param name="responseHeaders">The exchange's live response header collection.</param>
    /// <param name="features">The exchange's feature collection (drained for response cookies on accept).</param>
    /// <param name="kind">The detected transition kind (Upgrade or Connect).</param>
    /// <param name="protocol">The requested <c>Upgrade</c> protocol token, or <see langword="null"/> for CONNECT.</param>
    public Http1ProtocolUpgrade(
        IHttpConnectionTakeover takeover,
        HttpHeaderCollection responseHeaders,
        IHttpFeatureCollection features,
        HttpProtocolUpgradeKind kind,
        string? protocol)
    {
        _takeover = takeover;
        _responseHeaders = responseHeaders;
        _features = features;
        Kind = kind;
        Protocol = protocol;
    }

    /// <inheritdoc />
    public HttpProtocolUpgradeKind Kind { get; }

    /// <inheritdoc />
    public string? Protocol { get; }

    /// <inheritdoc />
    public async ValueTask<Stream> AcceptAsync(CancellationToken cancellationToken = default)
    {
        if (Interlocked.Exchange(ref _accepted, 1) == 1)
        {
            throw new InvalidOperationException(
                "The protocol upgrade has already been accepted for this exchange.");
        }

        // Resolve the status line before claiming the connection so an impossible kind fails
        // without side effects.
        HttpStatusCode status = Kind switch
        {
            HttpProtocolUpgradeKind.Upgrade => HttpStatusCode.SwitchingProtocols,
            HttpProtocolUpgradeKind.Connect => HttpStatusCode.Ok,
            _ => throw new InvalidOperationException($"Cannot accept a protocol upgrade of kind '{Kind}'."),
        };

        // Claim the connection before writing: from here the transport suppresses its own
        // response for the exchange and ends keep-alive, so even a cancelled or failed head
        // write cannot be followed by a second HTTP response on a desynchronized stream.
        Stream stream = _takeover.TakeOver();

        // RFC 9110 §9.3.6 — a successful CONNECT response MUST NOT include Content-Length or
        // Transfer-Encoding; the tunnel carries opaque octets. A 101 is body-less by definition
        // (RFC 9112 §9.9), so the same scrub applies. Strip both unconditionally.
        foreach (HttpHeaderKey key in ForbiddenResponseHeaders)
        {
            _responseHeaders.Remove(key);
        }

        switch (Kind)
        {
            case HttpProtocolUpgradeKind.Upgrade:
                // RFC 9110 §7.8 — a 101 response signals that the connection is switching to the
                // listed protocol. The Connection header MUST list Upgrade as a
                // connection-specific token; the Upgrade header MUST name the accepted protocol.
                _responseHeaders[HttpHeaderKey.Connection] = "Upgrade";
                if (!string.IsNullOrEmpty(Protocol))
                {
                    _responseHeaders[HttpHeaderKey.Upgrade] = Protocol;
                }
                break;

            case HttpProtocolUpgradeKind.Connect:
                // RFC 9110 §9.3.6 — once the 2xx response is sent, the connection becomes a
                // tunnel and persists for the lifetime of the request. We must not advertise
                // close even though the transport's keep-alive loop has ended (close applies to
                // HTTP framing, not the tunnel).
                _responseHeaders.Remove(HttpHeaderKey.Connection);
                break;
        }

        await WriteHeadAsync(stream, status, cancellationToken).ConfigureAwait(false);
        return stream;
    }

    private async ValueTask WriteHeadAsync(Stream stream, HttpStatusCode status, CancellationToken cancellationToken)
    {
        StringBuilder builder = new();
        // HttpStatusCode has an implicit conversion to int, which would steer overload
        // resolution toward StringBuilder.Append(int) and drop the reason phrase. Explicitly
        // stringify to preserve "101 Switching Protocols" / "200 Ok" on the status line.
        builder.Append("HTTP/1.1 ").Append(status.ToString()).Append("\r\n");

        foreach (System.Collections.Generic.KeyValuePair<HttpHeaderKey, HttpHeaderValue> header in _responseHeaders)
        {
            builder.Append(header.Key.ToString())
                .Append(": ")
                .Append(header.Value.ToString())
                .Append("\r\n");
        }

        // RFC 6265 §3 — each Set-Cookie value MUST be emitted on its own line. The cookie
        // feature is attached only when the response cookies extension has been used; otherwise
        // there are no cookies to drain.
        IHttpResponseCookieFeature? cookieFeature = _features.Get<IHttpResponseCookieFeature>();
        if (cookieFeature is not null)
        {
            foreach (HttpCookie cookie in cookieFeature.Cookies)
            {
                builder.Append(HttpHeaderKey.SetCookie.ToString())
                    .Append(": ")
                    .Append(cookie.ToString())
                    .Append("\r\n");
            }
        }

        builder.Append("\r\n");

        byte[] bytes = Encoding.ASCII.GetBytes(builder.ToString());
        await stream.WriteAsync(bytes.AsMemory(0, bytes.Length), cancellationToken).ConfigureAwait(false);
        await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
    }
}

using System;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace Assimalign.Cohesion.Http;

/// <summary>
/// HTTP/1.1 implementation of <see cref="IHttpProtocolUpgrade"/>. Owns the response
/// transition for both <c>Connection: upgrade</c> + <c>Upgrade</c> (101 Switching
/// Protocols) and <c>CONNECT</c> tunnels (200 OK).
/// </summary>
/// <remarks>
/// <para>
/// The instance is constructed by <see cref="Http1MessageReader"/> when a request
/// matches the upgrade or CONNECT signalling rules. <see cref="AcceptAsync"/> may
/// be invoked at most once per exchange — a second call surfaces as
/// <see cref="InvalidOperationException"/> instead of producing a second response
/// on the wire.
/// </para>
/// <para>
/// Acceptance writes the appropriate status line and the connection-specific
/// response headers (<c>Connection: Upgrade</c> + <c>Upgrade: &lt;protocol&gt;</c>
/// for an upgrade) and explicitly omits any framing headers (RFC 9110 §9.3.6
/// forbids <c>Content-Length</c> / <c>Transfer-Encoding</c> on a successful
/// CONNECT response, and a 1xx response has no body framing of its own). Any
/// other response headers and cookies the application set on the
/// <see cref="Http1Response"/> are emitted; the framing-related keys are
/// scrubbed unconditionally so the tunnel does not start with stale framing
/// metadata.
/// </para>
/// </remarks>
internal sealed class HttpProtocolUpgrade : IHttpProtocolUpgrade
{
    private static readonly HttpHeaderKey[] ForbiddenResponseHeaders =
    {
        HttpHeaderKey.ContentLength,
        HttpHeaderKey.TransferEncoding,
    };

    private readonly IHttpContext _context;
    private readonly Stream _stream;
    private int _accepted;

    public HttpProtocolUpgrade(
        IHttpContext context,
        Stream stream,
        HttpProtocolUpgradeKind kind,
        string? protocol)
    {
        _context = context;
        _stream = stream;
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

        IHttpResponse response = (IHttpResponse)_context.Response;
        IHttpHeaderCollection headers = response.Headers;

        // RFC 9110 §9.3.6 — a successful CONNECT response MUST NOT include
        // Content-Length or Transfer-Encoding; the tunnel carries opaque octets.
        // For a 101 the response is body-less by definition, so the same scrub
        // applies. Strip both unconditionally so neither leaks into the wire.
        foreach (HttpHeaderKey key in ForbiddenResponseHeaders)
        {
            headers.Remove(key);
        }

        switch (Kind)
        {
            case HttpProtocolUpgradeKind.Upgrade:
                response.StatusCode = HttpStatusCode.SwitchingProtocols;
                // RFC 9110 §7.8 — a 101 response signals that the connection is
                // switching to the listed protocol. The Connection header MUST list
                // Upgrade as a connection-specific token; the Upgrade header MUST
                // name the accepted protocol.
                headers[HttpHeaderKey.Connection] = "Upgrade";
                if (!string.IsNullOrEmpty(Protocol))
                {
                    headers[HttpHeaderKey.Upgrade] = Protocol;
                }
                break;

            case HttpProtocolUpgradeKind.Connect:
                if (response.StatusCode.Value == 0)
                {
                    response.StatusCode = HttpStatusCode.Ok;
                }
                // RFC 9110 §9.3.6 — once the 2xx response is sent, the connection
                // becomes a tunnel and persists for the lifetime of the request.
                // We must not advertise close here even though KeepAlive will be
                // false on the parent context (close applies to HTTP framing, not
                // the tunnel).
                headers.Remove(HttpHeaderKey.Connection);
                break;

            default:
                throw new InvalidOperationException(
                    $"Cannot accept a protocol upgrade of kind '{Kind}'.");
        }

        await WriteHeadersAsync(headers, response, cancellationToken).ConfigureAwait(false);

        //_context.KeepAlive = false;
        //_context.ResponseFinalized = true;
        return _stream;
    }

    private async ValueTask WriteHeadersAsync(
        IHttpHeaderCollection headers,
        IHttpResponse response,
        CancellationToken cancellationToken)
    {
        StringBuilder builder = new();
        // HttpStatusCode has an implicit conversion to int, which would steer the
        // overload resolution toward StringBuilder.Append(int) and drop the reason
        // phrase. Explicitly stringify to preserve "101 Switching Protocols" /
        // "200 Ok" on the status line.
        builder.Append("HTTP/1.1 ").Append(response.StatusCode.ToString()).Append("\r\n");

        foreach (System.Collections.Generic.KeyValuePair<HttpHeaderKey, HttpHeaderValue> header in headers)
        {
            builder.Append(header.Key.ToString())
                .Append(": ")
                .Append(header.Value.ToString())
                .Append("\r\n");
        }

        // RFC 6265 §3 — each Set-Cookie value MUST be emitted on its own line.
        // The cookie feature is attached only when the response cookies
        // extension has been used; otherwise there are no cookies to drain.
        IHttpResponseCookieFeature? cookieFeature = _context.Features.Get<IHttpResponseCookieFeature>();
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
        await _stream.WriteAsync(bytes.AsMemory(0, bytes.Length), cancellationToken).ConfigureAwait(false);
        await _stream.FlushAsync(cancellationToken).ConfigureAwait(false);
    }
}

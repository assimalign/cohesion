using System;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace Assimalign.Cohesion.Http.Internal;

/// <summary>
/// HTTP/1.1 implementation of <see cref="IHttpProtocolUpgrade"/>. Owns the response transition
/// for both <c>Connection: upgrade</c> + <c>Upgrade</c> (101 Switching Protocols) and
/// <c>CONNECT</c> tunnels (200 OK), built over the transport's generic
/// <see cref="IHttpExchangeControl"/> capability.
/// </summary>
/// <remarks>
/// <para>
/// Constructed by <see cref="HttpProtocolUpgradeInterceptor"/>'s response hook from
/// interceptor-seam materials only: the exchange control, the exchange's live response header
/// collection, and its feature collection (for response cookies). <see cref="AcceptAsync"/> may
/// be invoked at most once per exchange — a second call surfaces as
/// <see cref="InvalidOperationException"/> before any byte is written, so it can never produce a
/// second response on the wire.
/// </para>
/// <para>
/// Acceptance encodes the head first, checking every field line against the field syntax (#1183): a
/// name that is not a token, or a value holding CR, LF, NUL, or another control character but HTAB,
/// throws an <see cref="HttpException"/> with <see cref="HttpErrorCode.InvalidResponseField"/> before
/// the connection is claimed, so nothing is written and the exchange can still be answered with an
/// ordinary response. It then claims the connection (<see cref="IHttpExchangeControl.TakeOver"/> — from
/// that point the transport suppresses its own response and ends keep-alive) and writes the
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
    private static readonly HttpHeaderKey[] _forbiddenResponseHeaders =
    {
        HttpHeaderKey.ContentLength,
        HttpHeaderKey.TransferEncoding,
    };

    private readonly IHttpExchangeControl _control;
    private readonly HttpHeaderCollection _responseHeaders;
    private readonly IHttpFeatureCollection _features;
    private int _accepted;

    /// <summary>
    /// Initializes the upgrade for the current exchange.
    /// </summary>
    /// <param name="control">The transport's exchange control, whose takeover surrenders the connection.</param>
    /// <param name="responseHeaders">The exchange's live response header collection.</param>
    /// <param name="features">The exchange's feature collection (drained for response cookies on accept).</param>
    /// <param name="kind">The detected transition kind (Upgrade or Connect).</param>
    /// <param name="protocol">The requested <c>Upgrade</c> protocol token, or <see langword="null"/> for CONNECT.</param>
    public Http1ProtocolUpgrade(
        IHttpExchangeControl control,
        HttpHeaderCollection responseHeaders,
        IHttpFeatureCollection features,
        HttpProtocolUpgradeKind kind,
        string? protocol)
    {
        _control = control;
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

        // Encode the head before claiming the connection (#1183): a field it cannot carry is refused
        // while the exchange still belongs to the transport, with nothing written and the response
        // headers as the application staged them, so the exchange can still be answered with an
        // ordinary response. The accept is spent either way.
        byte[] head = EncodeHead(status);

        // Claim the connection before writing: from here the transport suppresses its own
        // response for the exchange and ends keep-alive, so even a cancelled or failed head
        // write cannot be followed by a second HTTP response on a desynchronized stream.
        Stream stream = _control.TakeOver();

        // The live headers now record the head that is sent.
        ApplyTransitionFields();

        await stream.WriteAsync(head, cancellationToken).ConfigureAwait(false);
        await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
        return stream;
    }

    /// <summary>
    /// Applies the transition's field rules to the live response headers, so the exchange reflects
    /// the head <see cref="EncodeHead"/> encoded.
    /// </summary>
    private void ApplyTransitionFields()
    {
        // RFC 9110 §9.3.6 — a successful CONNECT response MUST NOT include Content-Length or
        // Transfer-Encoding; the tunnel carries opaque octets. A 101 is body-less by definition
        // (RFC 9112 §9.9), so the same scrub applies. Strip both unconditionally.
        foreach (HttpHeaderKey key in _forbiddenResponseHeaders)
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
    }

    /// <summary>
    /// Encodes the transition head — the status line, the application's response fields under the
    /// rules <see cref="ApplyTransitionFields"/> applies, the response cookies, and the blank line —
    /// without changing the live headers. Each field line is checked against the field syntax before
    /// it is encoded (RFC 9110 §5.1, §5.5).
    /// </summary>
    /// <exception cref="HttpException">
    /// <see cref="HttpErrorCode.InvalidResponseField"/>: a field name is not a token, or a value holds
    /// a control character other than HTAB.
    /// </exception>
    private byte[] EncodeHead(HttpStatusCode status)
    {
        StringBuilder builder = new();
        // HttpStatusCode has an implicit conversion to int, which would steer overload
        // resolution toward StringBuilder.Append(int) and drop the reason phrase. Explicitly
        // stringify to preserve "101 Switching Protocols" / "200 Ok" on the status line.
        builder.Append("HTTP/1.1 ").Append(status.ToString()).Append("\r\n");

        bool replacesUpgrade = Kind == HttpProtocolUpgradeKind.Upgrade && !string.IsNullOrEmpty(Protocol);

        foreach (System.Collections.Generic.KeyValuePair<HttpHeaderKey, HttpHeaderValue> header in _responseHeaders)
        {
            // The fields ApplyTransitionFields removes or replaces are not the application's to send.
            if (header.Key == HttpHeaderKey.ContentLength
                || header.Key == HttpHeaderKey.TransferEncoding
                || header.Key == HttpHeaderKey.Connection
                || (replacesUpgrade && header.Key == HttpHeaderKey.Upgrade))
            {
                continue;
            }

            AppendFieldLine(builder, header.Key, header.Value.ToString());
        }

        if (Kind == HttpProtocolUpgradeKind.Upgrade)
        {
            AppendFieldLine(builder, HttpHeaderKey.Connection, "Upgrade");

            if (replacesUpgrade)
            {
                AppendFieldLine(builder, HttpHeaderKey.Upgrade, Protocol!);
            }
        }

        // RFC 6265 §3 — each Set-Cookie value MUST be emitted on its own line. The cookie
        // feature is attached only when the response cookies extension has been used; otherwise
        // there are no cookies to drain.
        IHttpResponseCookieFeature? cookieFeature = _features.Get<IHttpResponseCookieFeature>();
        if (cookieFeature is not null)
        {
            foreach (HttpCookie cookie in cookieFeature.Cookies)
            {
                AppendFieldLine(builder, HttpHeaderKey.SetCookie, cookie.ToString());
            }
        }

        builder.Append("\r\n");

        return Encoding.ASCII.GetBytes(builder.ToString());
    }

    /// <summary>
    /// Appends one field line once its name is a token and its value holds no control character
    /// other than HTAB: CR or LF would end the line early and start a field, or a response, of the
    /// value's own (CWE-113), and the transport's head writers refuse the same characters.
    /// </summary>
    private static void AppendFieldLine(StringBuilder builder, HttpHeaderKey key, string value)
    {
        string name = key.Value ?? string.Empty;

        if (!HttpFieldNormalization.IsValidFieldName(name))
        {
            // Not quoted: whatever makes the name invalid may be CR, LF, or NUL.
            throw new HttpInvalidResponseFieldException(
                "RFC 9110 §5.1: a response field name is not a token. The transition response was not sent.");
        }

        int invalid = HttpFieldNormalization.IndexOfInvalidControlCharacter(value);

        if (invalid >= 0)
        {
            throw new HttpInvalidResponseFieldException(
                $"RFC 9110 §5.5: the value of the response field '{name}' holds the control character 0x{(int)value[invalid]:X2} at index {invalid}; a field value holds no control character but HTAB. The transition response was not sent.");
        }

        builder.Append(name).Append(": ").Append(value).Append("\r\n");
    }
}

using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

namespace Assimalign.Cohesion.Http.Internal;

/// <summary>
/// The HTTP/1.1 opening handshake (RFC 6455 §4.2): a <c>GET</c> upgrade carrying
/// <c>Sec-WebSocket-Key</c> and <c>Sec-WebSocket-Version: 13</c>, answered with
/// <c>101 Switching Protocols</c> and <c>Sec-WebSocket-Accept</c> through the protocol-upgrade
/// takeover (<see cref="IHttpProtocolUpgrade"/>).
/// </summary>
/// <remarks>
/// <c>Connection: Upgrade</c> and <c>Upgrade: websocket</c> on the request are already checked: the
/// protocol-upgrade interceptor surfaces an upgrade only when both are present, and
/// <see cref="HttpWebSocketBootstrap.Select"/> checks the protocol. The <c>101</c> head, with its
/// <c>Connection</c> and <c>Upgrade</c> fields, is written by
/// <see cref="IHttpProtocolUpgrade.AcceptAsync"/>, which also scrubs body framing and claims the
/// connection before writing.
/// </remarks>
internal sealed class Http1WebSocketBootstrap : HttpWebSocketBootstrap
{
    private readonly IHttpRequest _request;
    private readonly IHttpProtocolUpgrade _upgrade;

    public Http1WebSocketBootstrap(IHttpRequest request, IHttpProtocolUpgrade upgrade)
    {
        _request = request;
        _upgrade = upgrade;
    }

    /// <inheritdoc />
    public override HttpWebSocketHandshakeStatus Validate()
    {
        // §4.2.1 item 1: the handshake is a GET. It carries no content: every octet after the head
        // belongs to the WebSocket once the connection switches, so content the transport would
        // otherwise read as a body would be taken for frames.
        if (_request.Method != HttpMethod.Get || HasContent(_request.Headers))
        {
            return HttpWebSocketHandshakeStatus.Invalid;
        }

        // §4.2.2 item 4, §4.4: a version the server does not speak is answered with 426 and the
        // supported version. Checked before the key, so a client of an earlier draft, which sends no
        // Sec-WebSocket-Key either, learns which version to retry with.
        if (!HttpWebSocketHandshake.HasSupportedVersion(_request.Headers))
        {
            return HttpWebSocketHandshakeStatus.UnsupportedVersion;
        }

        // §4.2.1 item 5: one key, the base64 encoding of a 16-byte nonce.
        if (!HttpWebSocketHandshake.TryGetKey(_request.Headers, out _))
        {
            return HttpWebSocketHandshakeStatus.Invalid;
        }

        return HttpWebSocketHandshakeStatus.Valid;
    }

    /// <inheritdoc />
    public override void ApplyAcceptFields(IHttpHeaderCollection responseHeaders)
    {
        // Validate() established that the key is present and well formed.
        HttpWebSocketHandshake.TryGetKey(_request.Headers, out string key);

        // §4.2.2 item 5.4.
        responseHeaders[HttpHeaderKey.SecWebSocketAccept] = HttpWebSocketHandshake.ComputeAcceptKey(key);
    }

    /// <inheritdoc />
    public override void ApplyUpgradeRequiredFields(IHttpHeaderCollection responseHeaders)
    {
        // RFC 9110 §15.5.22: a 426 names the protocol to upgrade to, and §7.8: an Upgrade field
        // travels with the "upgrade" connection option.
        responseHeaders[HttpHeaderKey.Upgrade] = HttpWebSocketHandshake.ProtocolToken;
        responseHeaders[HttpHeaderKey.Connection] = "Upgrade";
    }

    /// <inheritdoc />
    public override ValueTask<Stream> AcceptTransportAsync(CancellationToken cancellationToken)
    {
        return _upgrade.AcceptAsync(cancellationToken);
    }

    // RFC 9112 §6.3: a request has content when it is chunked or declares a non-zero length.
    private static bool HasContent(IHttpHeaderCollection headers)
    {
        if (headers.ContainsKey(HttpHeaderKey.TransferEncoding))
        {
            return true;
        }

        // SP and HTAB only: "0\xA0" is not a zero length, so it counts as content (#1341).
        return headers.TryGetValue(HttpHeaderKey.ContentLength, out HttpHeaderValue length)
            && !length.Value.AsSpan().Trim(HttpWebSocketHandshake.OptionalWhitespace).SequenceEqual("0");
    }
}

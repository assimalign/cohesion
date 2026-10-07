using System.IO;
using System.Threading;
using System.Threading.Tasks;

namespace Assimalign.Cohesion.Http.Internal;

/// <summary>
/// The HTTP/2 and HTTP/3 opening handshake (RFC 8441 §5, RFC 9220 §3): an extended CONNECT whose
/// <c>:protocol</c> is <c>websocket</c>, carrying <c>Sec-WebSocket-Version: 13</c>, answered with
/// <c>200</c> through the transport's tunnel accept (<see cref="IHttpExtendedConnectFeature"/>).
/// </summary>
/// <remarks>
/// <para>
/// The method and the <c>:protocol</c>, <c>:scheme</c>, <c>:path</c> and <c>:authority</c>
/// pseudo-headers are already checked: the transport installs
/// <see cref="IHttpExtendedConnectFeature"/> only on a valid extended CONNECT, and
/// <see cref="HttpWebSocketBootstrap.Select"/> checks the protocol. The stream's <c>DATA</c> is the
/// tunnel, so there is no request content to refuse, unlike the HTTP/1.1 <c>GET</c>.
/// </para>
/// <para>
/// RFC 8441 §5 retires the key and the accept value, whose job the <c>:protocol</c> pseudo-header
/// does: a <c>Sec-WebSocket-Key</c> the client sends is ignored, and no response carries
/// <c>Sec-WebSocket-Accept</c>. No response carries <c>Upgrade</c> or <c>Connection</c> either:
/// both are connection-specific, which HTTP/2 and HTTP/3 prohibit (RFC 9113 §8.2.2, RFC 9114 §4.2).
/// </para>
/// </remarks>
internal sealed class ExtendedConnectWebSocketBootstrap : HttpWebSocketBootstrap
{
    private readonly IHttpRequest _request;
    private readonly IHttpExtendedConnectFeature _extendedConnect;

    public ExtendedConnectWebSocketBootstrap(IHttpRequest request, IHttpExtendedConnectFeature extendedConnect)
    {
        _request = request;
        _extendedConnect = extendedConnect;
    }

    /// <inheritdoc />
    public override HttpWebSocketHandshakeStatus Validate()
    {
        // RFC 8441 §5 keeps Sec-WebSocket-Version: a version the server does not speak is answered
        // with 426 and the supported version (RFC 6455 §4.2.2 item 4, §4.4).
        return HttpWebSocketHandshake.HasSupportedVersion(_request.Headers)
            ? HttpWebSocketHandshakeStatus.Valid
            : HttpWebSocketHandshakeStatus.UnsupportedVersion;
    }

    /// <inheritdoc />
    public override void ApplyAcceptFields(IHttpHeaderCollection responseHeaders)
    {
        // RFC 8441 §5: the 200 carries no Sec-WebSocket-Accept. One the application staged is removed,
        // so the response never carries a field of the HTTP/1.1 handshake.
        responseHeaders.Remove(HttpHeaderKey.SecWebSocketAccept);
    }

    /// <inheritdoc />
    public override void ApplyUpgradeRequiredFields(IHttpHeaderCollection responseHeaders)
    {
        // Upgrade and Connection are connection-specific (RFC 9113 §8.2.2, RFC 9114 §4.2), so the 426
        // carries only the shared Sec-WebSocket-Version.
    }

    /// <inheritdoc />
    public override ValueTask<Stream> AcceptTransportAsync(CancellationToken cancellationToken)
    {
        return _extendedConnect.AcceptAsync(cancellationToken);
    }
}

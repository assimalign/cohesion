using System.Threading;

namespace Assimalign.Cohesion.Http.Connections.Internal;

internal sealed class Http2Context : TransportHttpContext
{
    public Http2Context(
        Http2Stream stream,
        in TransportHttpRequestHead requestHead,
        HttpConnectionInfo connectionInfo,
        CancellationToken requestAborted,
        IHttpFeatureCollection? features = null)
        : base(HttpVersion.Http20, requestHead, connectionInfo, requestAborted, features)
    {
        Stream = stream;
        ExtendedConnectProtocol = requestHead.Protocol;
    }

    public Http2Stream Stream { get; }

    public int StreamId => Stream.StreamId;

    /// <summary>
    /// The <c>:protocol</c> of a valid extended CONNECT (RFC 8441), or <see langword="null"/> for any
    /// other request. Only an exchange that carries one can accept a tunnel
    /// (<see cref="Http2ExchangeControl.AcceptTunnelAsync"/>).
    /// </summary>
    public string? ExtendedConnectProtocol { get; }

    /// <summary>
    /// The transport's request-body stream for this exchange — the innermost stream, independent of
    /// whatever the application later assigns to <see cref="HttpRequest.Body"/>. An accepted extended
    /// CONNECT tunnel reads the peer's <c>DATA</c> from it.
    /// </summary>
    public Http2RequestBodyStream? RequestBody { get; init; }

    /// <summary>
    /// The extended CONNECT tunnel accepted on this exchange, or <see langword="null"/>. Set by the
    /// accept path before the tunnel's response head is written.
    /// </summary>
    public Http2ExtendedConnectStream? Tunnel { get; set; }

    /// <summary>
    /// An accepted extended CONNECT tunnel takes the exchange's stream over, so the exchange reports
    /// <see cref="HttpExchangeDirective.TakeOver"/> and the raw response body sink refuses to commit a
    /// second head; otherwise the base's abort/continue derivation applies.
    /// </summary>
    internal override HttpExchangeDirective ExchangeDirective =>
        Tunnel is not null ? HttpExchangeDirective.TakeOver : base.ExchangeDirective;
}

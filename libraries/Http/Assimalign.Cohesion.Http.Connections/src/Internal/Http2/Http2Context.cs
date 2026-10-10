using System.Threading;
using System.Threading.Tasks;

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
    /// The connection that handed this exchange to the host, or <see langword="null"/> for an exchange
    /// it never handed over. Set by the frame pump at dispatch, so the exchange's disposal ends it on
    /// the connection (<see cref="Http2ConnectionContext.EndExchange"/>).
    /// </summary>
    public Http2ConnectionContext? Connection { get; set; }

    /// <summary>
    /// An accepted extended CONNECT tunnel takes the exchange's stream over, so the exchange reports
    /// <see cref="HttpExchangeDirective.TakeOver"/> and the raw response body sink refuses to commit a
    /// second head; otherwise the base's abort/continue derivation applies.
    /// </summary>
    internal override HttpExchangeDirective ExchangeDirective =>
        Tunnel is not null ? HttpExchangeDirective.TakeOver : base.ExchangeDirective;

    /// <summary>
    /// Disposes the exchange, then ends it on its connection. A host that never finalizes the exchange
    /// through <c>SendAsync</c> still releases, by disposing it, the concurrency slot a reset stream
    /// keeps while its exchange runs (RFC 9113 §5.1.2).
    /// </summary>
    public override async ValueTask DisposeAsync()
    {
        try
        {
            await base.DisposeAsync().ConfigureAwait(false);
        }
        finally
        {
            Connection?.EndExchange(Stream);
        }
    }
}

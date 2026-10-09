using System.Threading;

using Assimalign.Cohesion.Connections;

namespace Assimalign.Cohesion.Http.Connections.Internal;

internal sealed class Http3Context : TransportHttpContext
{
    /// <summary>
    /// Initializes the exchange for a decoded request head.
    /// </summary>
    /// <remarks>
    /// <see cref="HttpContext.RequestCancelled"/> fires on <paramref name="requestAborted"/> and on the
    /// request stream's <see cref="IConnection.ConnectionClosed"/>, which the drivers signal when the
    /// client resets the request stream (<c>RESET_STREAM</c>), stops the response
    /// (<c>STOP_SENDING</c>), or the stream is aborted or lost. RFC 9114 §4.1.1 — that is how a client
    /// cancels a request, so the application learns of it as an HTTP/2 <c>RST_STREAM</c> tells it,
    /// without having to read or write (#1329).
    /// </remarks>
    public Http3Context(
        in TransportHttpRequestHead requestHead,
        HttpConnectionInfo connectionInfo,
        CancellationToken requestAborted,
        IConnection streamConnection,
        long streamId,
        Http3RequestBodyStream requestBody,
        IHttpFeatureCollection? features = null)
        : base(HttpVersion.Http30, requestHead, connectionInfo, requestAborted, features, streamConnection.ConnectionClosed)
    {
        StreamConnection = streamConnection;
        StreamId = streamId;
        RequestBody = requestBody;
        ExtendedConnectProtocol = requestHead.Protocol;
    }

    /// <summary>
    /// The <c>:protocol</c> of a valid extended CONNECT (RFC 9220), or <see langword="null"/> for any
    /// other request. Only an exchange that carries one can accept a tunnel
    /// (<see cref="Http3ExchangeControl.AcceptTunnelAsync"/>).
    /// </summary>
    public string? ExtendedConnectProtocol { get; }

    /// <summary>
    /// The bidirectional QUIC stream this exchange arrived on; the response is written
    /// back to its output.
    /// </summary>
    public IConnection StreamConnection { get; }

    /// <summary>
    /// The request stream's wire ID (client-initiated bidirectional: 0, 4, 8, …), derived when the
    /// stream was accepted (see <see cref="Http3ConnectionContext"/>). Keys the QPACK decoder-stream
    /// instructions for the stream.
    /// </summary>
    public long StreamId { get; }

    /// <summary>
    /// The transport's request-body stream for this exchange — the innermost stream, independent of
    /// any wrapper a request interceptor installed on <see cref="HttpRequest.Body"/>. The send path
    /// consults it for a body-size rejection (413), for a transport reset, and to stop reading the
    /// request stream once the complete response is on the wire.
    /// </summary>
    public Http3RequestBodyStream RequestBody { get; }

    /// <summary>
    /// The effective RFC 9218 priority derived from this request's <c>Priority</c>
    /// header (urgency 3, non-incremental by default). HTTP/3 delegates cross-stream
    /// response ordering to the QUIC transport, so this is observable engine state
    /// rather than an input to an explicit scheduler (see docs/DESIGN.md).
    /// </summary>
    public HttpPriority EffectivePriority { get; set; } = HttpPriority.Default;

    /// <summary>
    /// The extended CONNECT tunnel accepted on this exchange (RFC 9220), or <see langword="null"/>. Set
    /// by the accept path before the tunnel's response head is written.
    /// </summary>
    public Http3ExtendedConnectStream? Tunnel { get; set; }

    /// <summary>
    /// An accepted extended CONNECT tunnel takes the exchange's request stream over, so the exchange
    /// reports <see cref="HttpExchangeDirective.TakeOver"/> and the raw response body sink refuses to
    /// commit a second head; otherwise the base's abort/continue derivation applies.
    /// </summary>
    internal override HttpExchangeDirective ExchangeDirective =>
        Tunnel is not null ? HttpExchangeDirective.TakeOver : base.ExchangeDirective;
}

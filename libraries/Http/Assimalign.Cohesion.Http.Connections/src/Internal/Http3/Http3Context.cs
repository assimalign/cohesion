using System.Threading;

using Assimalign.Cohesion.Connections;

namespace Assimalign.Cohesion.Http.Connections.Internal;

internal sealed class Http3Context : TransportHttpContext
{
    public Http3Context(
        in TransportHttpRequestHead requestHead,
        HttpConnectionInfo connectionInfo,
        CancellationToken requestAborted,
        IConnection streamConnection,
        long streamId,
        Http3RequestBodyStream requestBody,
        IHttpFeatureCollection? features = null)
        : base(HttpVersion.Http30, requestHead, connectionInfo, requestAborted, features)
    {
        StreamConnection = streamConnection;
        StreamId = streamId;
        RequestBody = requestBody;
    }

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

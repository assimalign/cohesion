using System.Threading;

using Assimalign.Cohesion.Connections;

namespace Assimalign.Cohesion.Http.Connections.Internal.Http3;

internal sealed class Http3Context : TransportHttpContext
{
    public Http3Context(
        Http3Request request,
        Http3Response response,
        HttpConnectionInfo connectionInfo,
        CancellationToken requestAborted,
        IConnection streamConnection,
        IHttpFeatureCollection? features = null)
        : base(HttpVersion.Http30, request, response, connectionInfo, requestAborted, features)
    {
        StreamConnection = streamConnection;
    }

    /// <summary>
    /// The bidirectional QUIC stream this exchange arrived on; the response is written
    /// back to its output.
    /// </summary>
    public IConnection StreamConnection { get; }

    /// <summary>
    /// The effective RFC 9218 priority derived from this request's <c>Priority</c>
    /// header (urgency 3, non-incremental by default). HTTP/3 delegates cross-stream
    /// response ordering to the QUIC transport, so this is observable engine state
    /// rather than an input to an explicit scheduler (see docs/DESIGN.md).
    /// </summary>
    public HttpPriority EffectivePriority { get; set; } = HttpPriority.Default;
}

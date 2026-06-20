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
        : base(HttpVersion.Http30, request, response, connectionInfo, requestAborted)
    {
        StreamConnection = streamConnection;
    }

    /// <summary>
    /// The bidirectional QUIC stream this exchange arrived on; the response is written
    /// back to its output.
    /// </summary>
    public IConnection StreamConnection { get; }
}

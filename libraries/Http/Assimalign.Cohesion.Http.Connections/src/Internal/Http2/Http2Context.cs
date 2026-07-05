using System.Threading;

namespace Assimalign.Cohesion.Http.Connections.Internal.Http2;

internal sealed class Http2Context : TransportHttpContext
{
    public Http2Context(
        Http2Stream stream,
        Http2Request request,
        Http2Response response,
        HttpConnectionInfo connectionInfo,
        CancellationToken requestAborted,
        IHttpFeatureCollection? features = null)
        : base(HttpVersion.Http20, request, response, connectionInfo, requestAborted, features)
    {
        Stream = stream;
    }

    public Http2Stream Stream { get; }

    public int StreamId => Stream.StreamId;
}

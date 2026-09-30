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
    }

    public Http2Stream Stream { get; }

    public int StreamId => Stream.StreamId;
}

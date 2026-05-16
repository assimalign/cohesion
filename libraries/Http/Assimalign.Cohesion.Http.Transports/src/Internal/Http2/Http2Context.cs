using System.Threading;

namespace Assimalign.Cohesion.Http.Transports.Internal.Http2;

internal sealed class Http2Context : TransportHttpContext
{
    public Http2Context(Http2Stream stream, Http2Request request, Http2Response response, HttpConnectionInfo connectionInfo, CancellationToken requestAborted)
        : base(HttpVersion.Http20, request, response, connectionInfo, requestAborted)
    {
        Stream = stream;
    }

    public Http2Stream Stream { get; }

    public int StreamId => Stream.StreamId;
}

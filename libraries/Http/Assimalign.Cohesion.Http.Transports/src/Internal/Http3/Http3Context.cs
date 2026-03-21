using System.Threading;

using Assimalign.Cohesion.Transports;

namespace Assimalign.Cohesion.Http.Transports.Internal.Http3;

internal sealed class Http3Context : TransportHttpContext
{
    public Http3Context(Http3Request request, Http3Response response, IHttpConnectionInfo connectionInfo, CancellationToken requestAborted, ITransportConnectionContext streamContext)
        : base(HttpVersion.Http30, request, response, connectionInfo, requestAborted)
    {
        StreamContext = streamContext;
    }

    public ITransportConnectionContext StreamContext { get; }
}

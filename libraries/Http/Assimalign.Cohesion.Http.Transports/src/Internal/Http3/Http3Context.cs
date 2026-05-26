using System.Threading;

using Assimalign.Cohesion.Transports;

namespace Assimalign.Cohesion.Http.Transports.Internal.Http3;

internal sealed class Http3Context : TransportHttpContext
{
    public Http3Context(
        Http3Request request,
        Http3Response response,
        HttpConnectionInfo connectionInfo,
        CancellationToken requestAborted,
        ITransportConnectionContext streamContext,
        IHttpFeatureCollection? features = null)
        : base(HttpVersion.Http30, request, response, connectionInfo, requestAborted, features)
    {
        StreamContext = streamContext;
    }

    public ITransportConnectionContext StreamContext { get; }
}

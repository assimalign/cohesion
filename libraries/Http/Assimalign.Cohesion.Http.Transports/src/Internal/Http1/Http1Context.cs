using System.Threading;

namespace Assimalign.Cohesion.Http.Transports.Internal.Http1;

internal sealed class Http1Context : TransportHttpContext
{
    public Http1Context(
        Http1Request request,
        Http1Response response,
        HttpConnectionInfo connectionInfo,
        CancellationToken requestAborted,
        bool keepAlive,
        IHttpFeatureCollection? features = null)
        : base(HttpVersion.Http11, request, response, connectionInfo, requestAborted, features)
    {
        KeepAlive = keepAlive;
    }

    public bool KeepAlive { get; set; }
}

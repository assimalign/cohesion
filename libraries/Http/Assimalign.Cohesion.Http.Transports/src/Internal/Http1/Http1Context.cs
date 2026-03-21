using System.Threading;

namespace Assimalign.Cohesion.Http.Transports.Internal.Http1;

internal sealed class Http1Context : TransportHttpContext
{
    public Http1Context(Http1Request request, Http1Response response, IHttpConnectionInfo connectionInfo, CancellationToken requestAborted, bool keepAlive)
        : base(HttpVersion.Http11, request, response, connectionInfo, requestAborted)
    {
        KeepAlive = keepAlive;
    }

    public bool KeepAlive { get; set; }
}

using System.Net;

using Assimalign.Cohesion.Connections;

namespace Assimalign.Cohesion.Http.Connections.Internal;

/// <summary>Connection-scoped diagnostic identity, without retaining the live driver.</summary>
internal class HttpTransportConnectionInfo : HttpConnectionInfo
{
    public HttpTransportConnectionInfo(EndPoint? localEndPoint, EndPoint? remoteEndPoint, ConnectionId connectionId)
        : base(localEndPoint, remoteEndPoint)
    {
        ConnectionId = connectionId;
    }

    internal ConnectionId ConnectionId { get; }
}

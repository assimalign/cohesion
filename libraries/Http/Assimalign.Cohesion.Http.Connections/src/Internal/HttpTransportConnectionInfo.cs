using System.Net;

using Assimalign.Cohesion.Connections;

namespace Assimalign.Cohesion.Http.Connections.Internal;

/// <summary>Connection-scoped diagnostic identity, without retaining the live driver.</summary>
[global::System.Diagnostics.DebuggerDisplay("HttpTransportConnectionInfo")]
[global::System.Diagnostics.DebuggerTypeProxy(typeof(DebugView))]
internal class HttpTransportConnectionInfo : HttpConnectionInfo
{
    public HttpTransportConnectionInfo(EndPoint? localEndPoint, EndPoint? remoteEndPoint, ConnectionId connectionId)
        : base(localEndPoint, remoteEndPoint)
    {
        ConnectionId = connectionId;
    }

    internal ConnectionId ConnectionId { get; }

    private sealed class DebugView
    {
        private readonly HttpTransportConnectionInfo _target;

        public DebugView(HttpTransportConnectionInfo value)
        {
            _target = value;
        }

        public global::System.Net.EndPoint? LocalEndPoint => _target.LocalEndPoint;
        public global::System.Net.EndPoint? RemoteEndPoint => _target.RemoteEndPoint;
        public global::System.Net.IPAddress? LocalIp => _target.LocalIp;
        public global::System.Net.IPAddress? RemoteIp => _target.RemoteIp;
        public int LocalPort => _target.LocalPort;
        public int RemotePort => _target.RemotePort;
        public global::System.Threading.CancellationToken ConnectionAborted => _target.ConnectionAborted;
    }
}

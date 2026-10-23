using System.Runtime.Versioning;

namespace Assimalign.Cohesion.Connections.Quic.Internal;

[global::System.Diagnostics.DebuggerDisplay("QuicConnectionStreamLimits {Name,nq}")]
[global::System.Diagnostics.DebuggerTypeProxy(typeof(DebugView))]
[SupportedOSPlatform("windows")]
[SupportedOSPlatform("linux")]
[SupportedOSPlatform("macos")]
internal sealed class QuicConnectionStreamLimits : IMultiplexedConnectionStreamLimits
{
    private readonly QuicMultiplexedConnection _connection;

    public QuicConnectionStreamLimits(QuicMultiplexedConnection connection)
    {
        _connection = connection;
    }

    public string Name => IMultiplexedConnectionStreamLimits.FeatureName;
    public long InboundBidirectionalStreamLimit => _connection.InboundBidirectionalStreamLimit;

    private sealed class DebugView
    {
        private readonly QuicConnectionStreamLimits _target;

        public DebugView(QuicConnectionStreamLimits value)
        {
            _target = value;
        }

        public string Name => _target.Name;
        public long InboundBidirectionalStreamLimit => _target.InboundBidirectionalStreamLimit;
    }
}

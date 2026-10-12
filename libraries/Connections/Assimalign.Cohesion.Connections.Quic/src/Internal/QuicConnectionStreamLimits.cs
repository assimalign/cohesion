using System.Runtime.Versioning;

namespace Assimalign.Cohesion.Connections.Quic.Internal;

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
}

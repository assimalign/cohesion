using System.Net;

namespace Assimalign.Cohesion.Connections;

/// <summary>Provides a multiplexed listener base with shared accept dispatch and live connection tracking.</summary>
public abstract class MultiplexedConnectionListener : ConnectionListener<IMultiplexedConnection>, IMultiplexedConnectionListener
{
    /// <inheritdoc />
    public abstract EndPoint EndPoint { get; }

    /// <inheritdoc />
    public abstract ConnectionCapabilities Capabilities { get; }
}

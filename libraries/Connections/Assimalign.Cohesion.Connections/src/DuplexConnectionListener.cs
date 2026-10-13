using System.Net;

namespace Assimalign.Cohesion.Connections;

/// <summary>Provides a duplex listener base with shared accept dispatch and live connection tracking.</summary>
public abstract class DuplexConnectionListener : ConnectionListener<IDuplexConnection>, IDuplexConnectionListener
{
    /// <inheritdoc />
    public abstract EndPoint EndPoint { get; }

    /// <inheritdoc />
    public abstract ConnectionCapabilities Capabilities { get; }
}

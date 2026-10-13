using System.Net;

namespace Assimalign.Cohesion.Connections;

/// <summary>Accepts multiplexed connections at one local endpoint.</summary>
public interface IMultiplexedConnectionListener : IConnectionListener<IMultiplexedConnection>
{
    /// <summary>Gets the configured local endpoint, or the actual bound endpoint after binding completes.</summary>
    EndPoint EndPoint { get; }

    /// <summary>Gets the capabilities of connections produced by this listener.</summary>
    ConnectionCapabilities Capabilities { get; }
}

using System.Net;
using System.Threading;
using System.Threading.Tasks;

namespace Assimalign.Cohesion.Connections;

/// <summary>
/// Establishes outbound multiplexed connections to a remote endpoint (the client side of a multiplexed transport).
/// </summary>
public interface IMultiplexedConnectionFactory
{
    /// <summary>
    /// Gets the capabilities of connections produced by this factory.
    /// </summary>
    ConnectionCapabilities Capabilities { get; }

    /// <summary>
    /// Connects to the specified remote endpoint.
    /// </summary>
    /// <param name="endPoint">The remote endpoint to connect to.</param>
    /// <param name="cancellationToken">A token to cancel the connect operation.</param>
    /// <returns>The established <see cref="IMultiplexedConnection"/>.</returns>
    ValueTask<IMultiplexedConnection> ConnectAsync(EndPoint endPoint, CancellationToken cancellationToken = default);
}

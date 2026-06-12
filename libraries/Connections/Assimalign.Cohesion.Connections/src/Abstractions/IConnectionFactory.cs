using System.Net;
using System.Threading;
using System.Threading.Tasks;

namespace Assimalign.Cohesion.Connections;

/// <summary>
/// Establishes outbound single-stream connections to a remote endpoint (the client side of a transport).
/// </summary>
public interface IConnectionFactory
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
    /// <returns>The established <see cref="IConnection"/>.</returns>
    ValueTask<IConnection> ConnectAsync(EndPoint endPoint, CancellationToken cancellationToken = default);
}

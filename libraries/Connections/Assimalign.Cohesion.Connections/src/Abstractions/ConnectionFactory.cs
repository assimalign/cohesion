using System.Net;
using System.Threading;
using System.Threading.Tasks;

namespace Assimalign.Cohesion.Connections;

/// <summary>
/// Provides the guided base class for implementing <see cref="IConnectionFactory"/>.
/// </summary>
/// <remarks>
/// Override <see cref="ConnectAsync(EndPoint, CancellationToken)"/> to return the concrete
/// <see cref="Connection"/>; the explicit <see cref="IConnectionFactory.ConnectAsync(EndPoint, CancellationToken)"/>
/// implementation forwards to it.
/// </remarks>
public abstract class ConnectionFactory : IConnectionFactory
{
    /// <inheritdoc />
    public abstract ConnectionCapabilities Capabilities { get; }

    /// <summary>
    /// Connects to the specified remote endpoint.
    /// </summary>
    /// <param name="endPoint">The remote endpoint to connect to.</param>
    /// <param name="cancellationToken">A token to cancel the connect operation.</param>
    /// <returns>The established <see cref="Connection"/>.</returns>
    public abstract ValueTask<Connection> ConnectAsync(EndPoint endPoint, CancellationToken cancellationToken = default);

    async ValueTask<IConnection> IConnectionFactory.ConnectAsync(EndPoint endPoint, CancellationToken cancellationToken)
        => await ConnectAsync(endPoint, cancellationToken).ConfigureAwait(false);
}

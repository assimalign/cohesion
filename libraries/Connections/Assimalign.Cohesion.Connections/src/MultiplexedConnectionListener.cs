using System.Net;
using System.Threading;
using System.Threading.Tasks;

namespace Assimalign.Cohesion.Connections;

/// <summary>
/// Provides the guided base class for implementing <see cref="IMultiplexedConnectionListener"/>.
/// </summary>
/// <remarks>
/// Override <see cref="AcceptAsync(CancellationToken)"/> to return the concrete <see cref="MultiplexedConnection"/>;
/// the explicit <see cref="IMultiplexedConnectionListener.AcceptAsync(CancellationToken)"/> implementation forwards to it.
/// </remarks>
public abstract class MultiplexedConnectionListener : IMultiplexedConnectionListener
{
    /// <inheritdoc />
    public abstract EndPoint EndPoint { get; }

    /// <inheritdoc />
    public abstract ConnectionCapabilities Capabilities { get; }

    /// <summary>
    /// Accepts the next inbound multiplexed connection.
    /// </summary>
    /// <param name="cancellationToken">A token to cancel the accept operation.</param>
    /// <returns>The accepted <see cref="MultiplexedConnection"/>.</returns>
    public abstract ValueTask<MultiplexedConnection> AcceptAsync(CancellationToken cancellationToken = default);

    /// <inheritdoc />
    public abstract ValueTask DisposeAsync();

    async ValueTask<IMultiplexedConnection> IMultiplexedConnectionListener.AcceptAsync(CancellationToken cancellationToken)
        => await AcceptAsync(cancellationToken).ConfigureAwait(false);
}

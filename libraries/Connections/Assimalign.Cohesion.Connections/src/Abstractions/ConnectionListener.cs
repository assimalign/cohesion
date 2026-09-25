using System.Net;
using System.Threading;
using System.Threading.Tasks;

namespace Assimalign.Cohesion.Connections;

/// <summary>
/// Provides the guided base class for implementing <see cref="IConnectionListener"/>.
/// </summary>
/// <remarks>
/// Override <see cref="AcceptAsync(CancellationToken)"/> to return the concrete <see cref="Connection"/>;
/// the explicit <see cref="IConnectionListener.AcceptAsync(CancellationToken)"/> implementation forwards to it.
/// </remarks>
public abstract class ConnectionListener : IConnectionListener
{
    /// <inheritdoc />
    public abstract EndPoint EndPoint { get; }

    /// <inheritdoc />
    public abstract ConnectionCapabilities Capabilities { get; }

    /// <inheritdoc />
    /// <remarks>
    /// The default implementation represents a listener that is logically bound when constructed or
    /// does not own an external endpoint. Resource-owning listeners override this member to acquire
    /// their endpoint explicitly.
    /// </remarks>
    public virtual ValueTask BindAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return ValueTask.CompletedTask;
    }

    /// <summary>
    /// Accepts the next inbound connection.
    /// </summary>
    /// <param name="cancellationToken">A token to cancel the accept operation.</param>
    /// <returns>The accepted <see cref="Connection"/>.</returns>
    public abstract ValueTask<Connection> AcceptAsync(CancellationToken cancellationToken = default);

    /// <inheritdoc />
    public abstract ValueTask DisposeAsync();

    async ValueTask<IConnection> IConnectionListener.AcceptAsync(CancellationToken cancellationToken)
        => await AcceptAsync(cancellationToken).ConfigureAwait(false);
}

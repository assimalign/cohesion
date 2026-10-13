using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

using Assimalign.Cohesion.Connections.Internal;

namespace Assimalign.Cohesion.Connections;

/// <summary>Provides one accept path and a live connection collection for a listener.</summary>
/// <typeparam name="TConnection">The reference type of connections the listener produces.</typeparam>
/// <remarks>
/// Implementations own binding and accepting; the base observes handed-out connections without
/// owning their disposal. The typed and untyped collection properties return the same object.
/// </remarks>
public abstract class ConnectionListener<TConnection> : IConnectionListener<TConnection> where TConnection : class, IConnection
{
    private readonly LiveConnectionCollection<TConnection> _connections = new();

    /// <inheritdoc />
    public abstract ListenerId Id { get; }

    /// <inheritdoc />
    public IReadOnlyCollection<TConnection> Connections => _connections;

    IReadOnlyCollection<IConnection> IConnectionListener.Connections => _connections;

    /// <inheritdoc />
    /// <remarks>The default represents a listener bound at construction and honors cancellation.</remarks>
    public virtual ValueTask BindAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return ValueTask.CompletedTask;
    }

    /// <inheritdoc />
    public async ValueTask<TConnection> AcceptAsync(CancellationToken cancellationToken = default)
    {
        TConnection connection = await AcceptCoreAsync(cancellationToken).ConfigureAwait(false);
        _connections.Track(connection);
        return connection;
    }

    /// <summary>Accepts the next connection from the listener's queue without tracking it.</summary>
    /// <param name="cancellationToken">A token that cancels this accept operation.</param>
    /// <returns>The connection whose ownership is transferred to the accepting caller.</returns>
    protected abstract ValueTask<TConnection> AcceptCoreAsync(CancellationToken cancellationToken = default);

    /// <summary>Releases the listener and unaccepted connections, leaving handed-out connections with their owners.</summary>
    /// <returns>A task that completes when listener resources have been released.</returns>
    public abstract ValueTask DisposeAsync();

    async ValueTask<IConnection> IConnectionListener.AcceptAsync(CancellationToken cancellationToken)
        => await AcceptAsync(cancellationToken).ConfigureAwait(false);
}

using System;
using System.Net;
using System.Threading;
using System.Threading.Tasks;

namespace Assimalign.Cohesion.Connections;

/// <summary>
/// Provides the guided base class for implementing <see cref="IMultiplexedConnection"/>.
/// </summary>
/// <remarks>
/// Override <see cref="AcceptStreamAsync(CancellationToken)"/> and
/// <see cref="OpenStreamAsync(ConnectionDirection, CancellationToken)"/> to return the concrete
/// <see cref="Connection"/> stream type; the explicit interface implementations forward to them.
/// </remarks>
public abstract class MultiplexedConnection : IMultiplexedConnection
{
    /// <inheritdoc />
    public abstract ConnectionId Id { get; }

    /// <inheritdoc />
    public abstract EndPoint? LocalEndPoint { get; }

    /// <inheritdoc />
    public abstract EndPoint? RemoteEndPoint { get; }

    /// <inheritdoc />
    public abstract ConnectionCapabilities Capabilities { get; }

    /// <inheritdoc />
    public abstract ConnectionState State { get; }

    /// <inheritdoc />
    public abstract CancellationToken ConnectionClosed { get; }

    /// <inheritdoc />
    public abstract void Abort(Exception? reason = null);

    /// <inheritdoc />
    public abstract ValueTask DisposeAsync();

    /// <summary>
    /// Accepts the next inbound stream opened by the remote peer.
    /// </summary>
    /// <param name="cancellationToken">A token to cancel the accept operation.</param>
    /// <returns>The accepted stream as a <see cref="Connection"/>.</returns>
    public abstract ValueTask<Connection> AcceptStreamAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Opens a new outbound stream to the remote peer.
    /// </summary>
    /// <param name="direction">
    /// The stream direction: <see cref="ConnectionDirection.Bidirectional"/> or
    /// <see cref="ConnectionDirection.WriteOnly"/> for an outbound unidirectional stream.
    /// </param>
    /// <param name="cancellationToken">A token to cancel the open operation.</param>
    /// <returns>The opened stream as a <see cref="Connection"/>.</returns>
    public abstract ValueTask<Connection> OpenStreamAsync(ConnectionDirection direction = ConnectionDirection.Bidirectional, CancellationToken cancellationToken = default);

    async ValueTask<IConnection> IMultiplexedConnection.AcceptStreamAsync(CancellationToken cancellationToken)
        => await AcceptStreamAsync(cancellationToken).ConfigureAwait(false);

    async ValueTask<IConnection> IMultiplexedConnection.OpenStreamAsync(ConnectionDirection direction, CancellationToken cancellationToken)
        => await OpenStreamAsync(direction, cancellationToken).ConfigureAwait(false);
}

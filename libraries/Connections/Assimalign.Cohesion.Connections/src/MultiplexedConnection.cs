using System;
using System.Threading;
using System.Threading.Tasks;

namespace Assimalign.Cohesion.Connections;

/// <summary>
/// Provides the guided base class for implementing <see cref="IMultiplexedConnection"/>.
/// </summary>
/// <remarks>
/// Override <see cref="AcceptStreamAsync(CancellationToken)"/> and
/// <see cref="OpenStreamAsync(ConnectionDirection, CancellationToken)"/> to return the concrete
/// <see cref="MultiplexedConnectionStream"/> stream type; the explicit interface implementations forward to them.
/// </remarks>
public abstract class MultiplexedConnection : Connection, IMultiplexedConnection
{
    /// <inheritdoc />
    public abstract void Abort(long errorCode, Exception? reason = null);

    /// <summary>
    /// Accepts the next inbound stream opened by the remote peer.
    /// </summary>
    /// <param name="cancellationToken">A token to cancel the accept operation.</param>
    /// <returns>The accepted stream as a <see cref="MultiplexedConnectionStream"/>.</returns>
    /// <remarks>
    /// A transport that supports only bidirectional streams never returns a
    /// <see cref="ConnectionDirection.ReadOnly"/> stream.
    /// </remarks>
    public abstract ValueTask<MultiplexedConnectionStream> AcceptStreamAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Opens a new outbound stream to the remote peer.
    /// </summary>
    /// <param name="direction">
    /// The stream direction: <see cref="ConnectionDirection.Bidirectional"/> or
    /// <see cref="ConnectionDirection.WriteOnly"/> for an outbound unidirectional stream.
    /// </param>
    /// <param name="cancellationToken">A token to cancel the open operation.</param>
    /// <returns>The opened stream as a <see cref="MultiplexedConnectionStream"/>.</returns>
    /// <exception cref="ArgumentException">
    /// <paramref name="direction"/> is <see cref="ConnectionDirection.ReadOnly"/>.
    /// </exception>
    /// <exception cref="NotSupportedException">
    /// <paramref name="direction"/> is <see cref="ConnectionDirection.WriteOnly"/> and the transport
    /// supports only bidirectional streams.
    /// </exception>
    public abstract ValueTask<MultiplexedConnectionStream> OpenStreamAsync(ConnectionDirection direction = ConnectionDirection.Bidirectional, CancellationToken cancellationToken = default);

    async ValueTask<IMultiplexedConnectionStream> IMultiplexedConnection.AcceptStreamAsync(CancellationToken cancellationToken)
        => await AcceptStreamAsync(cancellationToken).ConfigureAwait(false);

    async ValueTask<IMultiplexedConnectionStream> IMultiplexedConnection.OpenStreamAsync(ConnectionDirection direction, CancellationToken cancellationToken)
        => await OpenStreamAsync(direction, cancellationToken).ConfigureAwait(false);

}

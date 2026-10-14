using System;
using System.Threading;
using System.Threading.Tasks;

namespace Assimalign.Cohesion.Connections;

/// <summary>
/// Represents a connection that carries multiple independent <see cref="IMultiplexedConnectionStream"/> streams
/// over a single underlying transport (for example, a QUIC connection).
/// </summary>
/// <remarks>
/// Each accepted or opened stream is an <see cref="IMultiplexedConnectionStream"/> with its own lifetime,
/// duplex pipe, and <see cref="IDuplexConnection.Direction"/>. Disposing the multiplexed connection
/// tears down all of its streams.
/// </remarks>
public interface IMultiplexedConnection : IConnection
{
    /// <summary>
    /// Accepts the next inbound stream opened by the remote peer.
    /// </summary>
    /// <param name="cancellationToken">A token to cancel the accept operation.</param>
    /// <returns>
    /// The accepted stream as an <see cref="IMultiplexedConnectionStream"/>; inspect <see cref="IDuplexConnection.Direction"/>
    /// to determine whether the peer opened it as bidirectional or unidirectional.
    /// </returns>
    /// <remarks>
    /// A transport that supports only bidirectional streams never returns a stream whose
    /// <see cref="IDuplexConnection.Direction"/> is <see cref="ConnectionDirection.ReadOnly"/>.
    /// </remarks>
    ValueTask<IMultiplexedConnectionStream> AcceptStreamAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Opens a new outbound stream to the remote peer.
    /// </summary>
    /// <param name="direction">
    /// The stream direction: <see cref="ConnectionDirection.Bidirectional"/> for a stream both sides
    /// write to, or <see cref="ConnectionDirection.WriteOnly"/> for an outbound unidirectional stream.
    /// </param>
    /// <param name="cancellationToken">A token to cancel the open operation.</param>
    /// <returns>The opened stream as an <see cref="IMultiplexedConnectionStream"/>.</returns>
    /// <exception cref="ArgumentException">
    /// Thrown when <paramref name="direction"/> is <see cref="ConnectionDirection.ReadOnly"/>; a peer
    /// cannot open a stream that only the remote side writes to.
    /// </exception>
    /// <exception cref="NotSupportedException">
    /// Thrown when <paramref name="direction"/> is <see cref="ConnectionDirection.WriteOnly"/> and
    /// the transport supports only bidirectional streams.
    /// </exception>
    ValueTask<IMultiplexedConnectionStream> OpenStreamAsync(ConnectionDirection direction = ConnectionDirection.Bidirectional, CancellationToken cancellationToken = default);

    /// <summary>
    /// Aborts the connection and all of its streams immediately, closing it with <paramref name="errorCode"/>.
    /// </summary>
    /// <param name="errorCode">The transport-specific application error code sent to the peer when supported.</param>
    /// <param name="reason">An optional exception describing why the connection was aborted.</param>
    /// <remarks>
    /// The first abort or disposal decides the close; later calls do not change it. The state becomes
    /// <see cref="ConnectionState.Aborted"/>, <see cref="ConnectionClosed"/> is signaled, and every stream ends.
    /// A transport that cannot carry a close code closes as <see cref="Abort(Exception)"/> does.
    /// </remarks>
    /// <exception cref="ArgumentOutOfRangeException">
    /// <paramref name="errorCode"/> is negative or outside the range supported by the transport.
    /// </exception>
    void Abort(long errorCode, Exception? reason = null);
}

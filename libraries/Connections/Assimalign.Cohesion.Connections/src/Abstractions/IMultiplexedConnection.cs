using System;
using System.Net;
using System.Threading;
using System.Threading.Tasks;

namespace Assimalign.Cohesion.Connections;

/// <summary>
/// Represents a connection that carries multiple independent <see cref="IConnection"/> streams
/// over a single underlying transport (for example, a QUIC connection).
/// </summary>
/// <remarks>
/// Each accepted or opened stream is a full <see cref="IConnection"/> with its own lifetime,
/// duplex pipe, and <see cref="IConnection.Direction"/>. Disposing the multiplexed connection
/// tears down all of its streams.
/// </remarks>
public interface IMultiplexedConnection : IAsyncDisposable
{
    /// <summary>
    /// Gets the unique identifier for this connection.
    /// </summary>
    ConnectionId Id { get; }

    /// <summary>
    /// Gets the local endpoint the connection is bound to, or <see langword="null"/> when not applicable.
    /// </summary>
    EndPoint? LocalEndPoint { get; }

    /// <summary>
    /// Gets the remote endpoint the connection is connected to, or <see langword="null"/> when not applicable.
    /// </summary>
    EndPoint? RemoteEndPoint { get; }

    /// <summary>
    /// Gets the capabilities (delivery guarantees, multiplexing, and security) of the underlying transport.
    /// </summary>
    ConnectionCapabilities Capabilities { get; }

    /// <summary>
    /// Gets the current lifecycle state of the connection.
    /// </summary>
    ConnectionState State { get; }

    /// <summary>
    /// Gets a token that is signaled when the connection is closed or aborted.
    /// </summary>
    CancellationToken ConnectionClosed { get; }

    /// <summary>
    /// Accepts the next inbound stream opened by the remote peer.
    /// </summary>
    /// <param name="cancellationToken">A token to cancel the accept operation.</param>
    /// <returns>
    /// The accepted stream as an <see cref="IConnection"/>; inspect <see cref="IConnection.Direction"/>
    /// to determine whether the peer opened it as bidirectional or unidirectional.
    /// </returns>
    ValueTask<IConnection> AcceptStreamAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Opens a new outbound stream to the remote peer.
    /// </summary>
    /// <param name="direction">
    /// The stream direction: <see cref="ConnectionDirection.Bidirectional"/> for a stream both sides
    /// write to, or <see cref="ConnectionDirection.WriteOnly"/> for an outbound unidirectional stream.
    /// </param>
    /// <param name="cancellationToken">A token to cancel the open operation.</param>
    /// <returns>The opened stream as an <see cref="IConnection"/>.</returns>
    /// <exception cref="ArgumentException">
    /// Thrown when <paramref name="direction"/> is <see cref="ConnectionDirection.ReadOnly"/>; a peer
    /// cannot open a stream that only the remote side writes to.
    /// </exception>
    ValueTask<IConnection> OpenStreamAsync(ConnectionDirection direction = ConnectionDirection.Bidirectional, CancellationToken cancellationToken = default);

    /// <summary>
    /// Aborts the connection and all of its streams immediately.
    /// </summary>
    /// <param name="reason">An optional exception describing why the connection was aborted.</param>
    void Abort(Exception? reason = null);
}

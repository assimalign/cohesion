using System;
using System.Net;
using System.Threading;
using System.Threading.Tasks;

namespace Assimalign.Cohesion.Connections;

/// <summary>
/// Represents a message-oriented connection that sends and receives discrete datagrams
/// (for example, UDP), preserving message boundaries rather than exposing a byte stream.
/// </summary>
public interface IDatagramConnection : IAsyncDisposable
{
    /// <summary>
    /// Gets the local endpoint the connection is bound to.
    /// </summary>
    EndPoint LocalEndPoint { get; }

    /// <summary>
    /// Gets the remote endpoint for a connected datagram socket, or <see langword="null"/> when unbound.
    /// </summary>
    EndPoint? RemoteEndPoint { get; }

    /// <summary>
    /// Gets the capabilities (delivery guarantees and security) of the underlying transport.
    /// </summary>
    ConnectionCapabilities Capabilities { get; }

    /// <summary>
    /// Receives the next datagram into the supplied buffer.
    /// </summary>
    /// <param name="buffer">The buffer to copy the received datagram into.</param>
    /// <param name="cancellationToken">A token to cancel the receive operation.</param>
    /// <returns>A <see cref="DatagramReceiveResult"/> describing the received datagram.</returns>
    ValueTask<DatagramReceiveResult> ReceiveAsync(Memory<byte> buffer, CancellationToken cancellationToken = default);

    /// <summary>
    /// Sends a datagram to the specified remote endpoint.
    /// </summary>
    /// <param name="payload">The datagram payload to send.</param>
    /// <param name="remoteEndPoint">The endpoint to send the datagram to.</param>
    /// <param name="cancellationToken">A token to cancel the send operation.</param>
    /// <returns>A task that completes when the datagram has been handed to the transport.</returns>
    ValueTask SendAsync(ReadOnlyMemory<byte> payload, EndPoint remoteEndPoint, CancellationToken cancellationToken = default);
}

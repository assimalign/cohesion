using System.Collections.Generic;
using System.IO;
using System.IO.Pipelines;
using System.Net;
using System.Threading;
using System.Threading.Tasks;

namespace Assimalign.Cohesion.Amqp.Connections;

/// <summary>
/// Represents an opened AMQP connection context that can negotiate protocol headers and exchange frames.
/// </summary>
/// <remarks>
/// The context is a duplex pipe over the carrier connection: <see cref="IDuplexPipe.Input"/> yields
/// the bytes received from the remote peer, and <see cref="IDuplexPipe.Output"/> accepts the bytes
/// to send to it.
/// </remarks>
public interface IAmqpConnectionContext : IDuplexPipe
{
    /// <summary>
    /// Gets the local endpoint of the carrier connection, or <see langword="null"/> when not applicable.
    /// </summary>
    EndPoint? LocalEndPoint { get; }

    /// <summary>
    /// Gets the remote endpoint of the carrier connection, or <see langword="null"/> when not applicable.
    /// </summary>
    EndPoint? RemoteEndPoint { get; }

    /// <summary>
    /// Gets the local AMQP protocol header that will be sent for the current protocol phase.
    /// </summary>
    AmqpProtocolHeader LocalProtocolHeader { get; }

    /// <summary>
    /// Gets the remote AMQP protocol header received for the current protocol phase.
    /// </summary>
    AmqpProtocolHeader? RemoteProtocolHeader { get; }

    /// <summary>
    /// Lazily adapts the context's duplex pipe as a bidirectional <see cref="Stream"/>.
    /// </summary>
    /// <returns>A <see cref="Stream"/> reading from the context's input and writing to its output.</returns>
    Stream AsStream();

    /// <summary>
    /// Performs the AMQP protocol header negotiation for the current protocol phase.
    /// </summary>
    /// <param name="cancellationToken">The cancellation token for the negotiation.</param>
    /// <returns>The negotiated remote AMQP protocol header.</returns>
    ValueTask<AmqpProtocolHeader> NegotiateAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Switches the current protocol phase by sending a new AMQP protocol header and reading the corresponding remote header.
    /// </summary>
    /// <param name="protocolHeader">The protocol header for the next phase.</param>
    /// <param name="cancellationToken">The cancellation token for the negotiation.</param>
    /// <returns>The negotiated remote AMQP protocol header.</returns>
    ValueTask<AmqpProtocolHeader> SwitchProtocolAsync(
        AmqpProtocolHeader protocolHeader,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Receives AMQP frames from the carrier connection.
    /// </summary>
    /// <param name="cancellationToken">The cancellation token for the receive operation.</param>
    /// <returns>An asynchronous sequence of decoded AMQP frames.</returns>
    IAsyncEnumerable<AmqpFrame> ReceiveAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Sends an AMQP frame to the carrier connection.
    /// </summary>
    /// <param name="frame">The AMQP frame to send.</param>
    /// <param name="cancellationToken">The cancellation token for the send operation.</param>
    /// <returns>A task that completes when the frame has been written and flushed.</returns>
    ValueTask SendAsync(AmqpFrame frame, CancellationToken cancellationToken = default);
}

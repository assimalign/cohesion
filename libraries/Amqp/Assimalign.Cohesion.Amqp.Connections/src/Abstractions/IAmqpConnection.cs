using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

using Assimalign.Cohesion.Connections;

namespace Assimalign.Cohesion.Amqp.Connections;

/// <summary>
/// Represents an established AMQP connection: a duplex connection whose byte stream carries AMQP
/// protocol headers and frames.
/// </summary>
/// <remarks>
/// <para>
/// A connection is ready when its listener or factory returns it. With
/// <see cref="AmqpConnectionListenerOptions.AutoNegotiateProtocolHeader"/> or
/// <see cref="AmqpConnectionFactoryOptions.AutoNegotiateProtocolHeader"/> enabled (the default), the
/// protocol headers have already been exchanged and frames can be sent and received at once.
/// Otherwise call <see cref="NegotiateAsync(CancellationToken)"/> before exchanging frames.
/// </para>
/// <para>
/// The connection is a duplex pipe over its carrier: <see cref="System.IO.Pipelines.IDuplexPipe.Input"/>
/// yields the bytes received from the remote peer and <see cref="System.IO.Pipelines.IDuplexPipe.Output"/>
/// accepts the bytes to send. Reading or writing them directly bypasses the frame members, so a
/// consumer uses one or the other for a given direction.
/// </para>
/// </remarks>
public interface IAmqpConnection : IDuplexConnection
{
    /// <summary>
    /// Gets the local AMQP protocol header sent for the current protocol phase.
    /// </summary>
    AmqpProtocolHeader LocalProtocolHeader { get; }

    /// <summary>
    /// Gets the remote AMQP protocol header received for the current protocol phase, or
    /// <see langword="null"/> when the headers for that phase have not been exchanged.
    /// </summary>
    AmqpProtocolHeader? RemoteProtocolHeader { get; }

    /// <summary>
    /// Exchanges the AMQP protocol headers for the current protocol phase, or returns the remote header
    /// when they have already been exchanged.
    /// </summary>
    /// <param name="cancellationToken">The cancellation token for the negotiation.</param>
    /// <returns>The negotiated remote AMQP protocol header.</returns>
    /// <exception cref="AmqpProtocolException">Thrown when the carrier ends before the remote header is received, or the header is invalid.</exception>
    ValueTask<AmqpProtocolHeader> NegotiateAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Switches to the next protocol phase by sending a new AMQP protocol header and reading the
    /// corresponding remote header (for example, from the SASL layer to AMQP).
    /// </summary>
    /// <param name="protocolHeader">The protocol header for the next phase.</param>
    /// <param name="cancellationToken">The cancellation token for the negotiation.</param>
    /// <returns>The negotiated remote AMQP protocol header.</returns>
    /// <exception cref="AmqpProtocolException">Thrown when the carrier ends before the remote header is received, or the header is invalid.</exception>
    ValueTask<AmqpProtocolHeader> SwitchProtocolAsync(
        AmqpProtocolHeader protocolHeader,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Receives AMQP frames from the carrier until the remote peer ends its output.
    /// </summary>
    /// <param name="cancellationToken">The cancellation token for the receive operation.</param>
    /// <returns>An asynchronous sequence of decoded AMQP frames.</returns>
    /// <exception cref="InvalidOperationException">Thrown when the protocol headers for the current phase have not been exchanged.</exception>
    /// <exception cref="AmqpProtocolException">Thrown when the carrier ends inside a frame or a frame is invalid.</exception>
    IAsyncEnumerable<AmqpFrame> ReceiveAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Encodes an AMQP frame and writes it to the carrier.
    /// </summary>
    /// <param name="frame">The AMQP frame to send.</param>
    /// <param name="cancellationToken">The cancellation token for the send operation.</param>
    /// <returns>A task that completes when the frame has been written and flushed.</returns>
    /// <exception cref="InvalidOperationException">Thrown when the protocol headers for the current phase have not been exchanged.</exception>
    /// <exception cref="AmqpProtocolException">Thrown when the encoded frame exceeds the configured maximum frame size.</exception>
    ValueTask SendAsync(AmqpFrame frame, CancellationToken cancellationToken = default);
}

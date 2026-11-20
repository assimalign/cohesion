using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace Assimalign.Cohesion.Amqp;

/// <summary>
/// Represents a complete delivery a receiver took from its link, and the receiver's means of settling it
/// (AMQP 1.0 Part 2, section 2.6.12).
/// </summary>
/// <remarks>
/// <para>
/// Under <see cref="AmqpReceiverSettleMode.First"/> a settlement call sends the outcome settled and
/// completes once the frame is queued. Under <see cref="AmqpReceiverSettleMode.Second"/> it sends the
/// outcome unsettled and completes when the sender settles. A delivery the sender sent settled needs no
/// outcome, so settling it does nothing. Only the first settlement call has an effect.
/// </para>
/// </remarks>
public interface IAmqpDelivery
{
    /// <summary>
    /// Gets the receiver the delivery arrived on.
    /// </summary>
    IAmqpReceiver Receiver { get; }

    /// <summary>
    /// Gets the delivery tag, which identifies the delivery on its link.
    /// </summary>
    AmqpDeliveryTag DeliveryTag { get; }

    /// <summary>
    /// Gets the message format of the payload; 0 is the AMQP 1.0 message format.
    /// </summary>
    uint MessageFormat { get; }

    /// <summary>
    /// Gets the payload: the bytes of every <c>transfer</c> frame of the delivery, in order.
    /// </summary>
    ReadOnlyMemory<byte> Payload { get; }

    /// <summary>
    /// Gets a value indicating whether the delivery is settled: the sender sent it settled, or settlement
    /// completed.
    /// </summary>
    bool IsSettled { get; }

    /// <summary>
    /// Gets the delivery state the sender's <c>transfer</c> carried, or <see langword="null"/>; for a
    /// transactional send, an <see cref="AmqpTransactionalState"/>.
    /// </summary>
    AmqpDeliveryState? RemoteState { get; }

    /// <summary>
    /// Gets a value indicating whether the sender resumed the delivery from an earlier attachment of the
    /// link (AMQP 1.0 Part 2, section 2.6.13), such as one <see cref="AmqpLinkOptions.Unsettled"/> listed.
    /// </summary>
    bool IsResumed { get; }

    /// <summary>
    /// Decodes the payload as an AMQP 1.0 message.
    /// </summary>
    /// <returns>The decoded message.</returns>
    /// <exception cref="AmqpException">Thrown with <see cref="AmqpErrorCode.ProtocolViolation"/> when the payload is not a valid AMQP message.</exception>
    AmqpMessage GetMessage();

    /// <summary>
    /// Settles the delivery with the <see cref="AmqpAccepted"/> outcome.
    /// </summary>
    /// <param name="cancellationToken">A token to cancel the wait for the sender's settlement.</param>
    /// <returns>A task that completes when the delivery is settled.</returns>
    /// <exception cref="AmqpException">Thrown with <see cref="AmqpErrorCode.LinkDetached"/> or <see cref="AmqpErrorCode.SessionEnded"/> when the delivery can no longer be settled.</exception>
    ValueTask AcceptAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Settles the delivery with the <see cref="AmqpRejected"/> outcome.
    /// </summary>
    /// <param name="error">The reason for the rejection, or <see langword="null"/>.</param>
    /// <param name="cancellationToken">A token to cancel the wait for the sender's settlement.</param>
    /// <returns>A task that completes when the delivery is settled.</returns>
    /// <exception cref="AmqpException">Thrown with <see cref="AmqpErrorCode.LinkDetached"/> or <see cref="AmqpErrorCode.SessionEnded"/> when the delivery can no longer be settled.</exception>
    ValueTask RejectAsync(AmqpError? error = null, CancellationToken cancellationToken = default);

    /// <summary>
    /// Settles the delivery with the <see cref="AmqpReleased"/> outcome.
    /// </summary>
    /// <param name="cancellationToken">A token to cancel the wait for the sender's settlement.</param>
    /// <returns>A task that completes when the delivery is settled.</returns>
    /// <exception cref="AmqpException">Thrown with <see cref="AmqpErrorCode.LinkDetached"/> or <see cref="AmqpErrorCode.SessionEnded"/> when the delivery can no longer be settled.</exception>
    ValueTask ReleaseAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Settles the delivery with the <see cref="AmqpModified"/> outcome.
    /// </summary>
    /// <param name="deliveryFailed">Whether the source counts the attempt as a failed delivery.</param>
    /// <param name="undeliverableHere">Whether the source must not redeliver the message to this link.</param>
    /// <param name="messageAnnotations">The message annotations the source merges into the message, or <see langword="null"/>.</param>
    /// <param name="cancellationToken">A token to cancel the wait for the sender's settlement.</param>
    /// <returns>A task that completes when the delivery is settled.</returns>
    /// <exception cref="AmqpException">Thrown with <see cref="AmqpErrorCode.LinkDetached"/> or <see cref="AmqpErrorCode.SessionEnded"/> when the delivery can no longer be settled.</exception>
    ValueTask ModifyAsync(
        bool deliveryFailed,
        bool undeliverableHere = false,
        IReadOnlyDictionary<AmqpSymbol, object?>? messageAnnotations = null,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Settles the delivery with a delivery state of the application's choosing.
    /// </summary>
    /// <param name="state">The outcome or state to send, or <see langword="null"/> to settle without one.</param>
    /// <param name="cancellationToken">A token to cancel the wait for the sender's settlement.</param>
    /// <returns>A task that completes when the delivery is settled.</returns>
    /// <exception cref="AmqpException">Thrown with <see cref="AmqpErrorCode.LinkDetached"/> or <see cref="AmqpErrorCode.SessionEnded"/> when the delivery can no longer be settled.</exception>
    ValueTask SettleAsync(AmqpDeliveryState? state, CancellationToken cancellationToken = default);
}

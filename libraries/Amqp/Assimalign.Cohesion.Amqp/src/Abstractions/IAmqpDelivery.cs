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
/// <para>
/// A delivery stays live while its link is suspended (detached without closing and without an error): it
/// can still be settled, and a <c>disposition</c> the sender sends still applies to it (AMQP 1.0 Part 2,
/// section 2.7.6). It can no longer be settled once the link closes or detaches with an error, attaches
/// again under its name on the session, or its session ends.
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
    /// Gets a value indicating whether the sender resumed the delivery from an earlier attachment of the link:
    /// one the link's <see cref="IAmqpLink.UnsettledReconciliation"/> lists as
    /// <see cref="AmqpUnsettledReconciliation.Resumable"/> (AMQP 1.0 Part 2, section 2.6.13).
    /// </summary>
    /// <remarks>
    /// <para>
    /// A resumed delivery carries the sender's state in <see cref="RemoteState"/>, and may carry part of the
    /// message or none of it. An <see cref="AmqpReceived"/> state names the section and offset its payload starts
    /// at; a payload with no state starts at the beginning of the message; a terminal outcome with no payload
    /// restates the outcome at the sender, which the receiver echoes when it settles (AMQP 1.0 Part 3,
    /// section 3.4.6).
    /// </para>
    /// <para>
    /// The link keeps no payload across attachments: the application joins a resumed payload to the data it kept
    /// of the delivery, skipping what it already has. A resume of a delivery the receiver did not list is
    /// ignored, as section 2.6.13 requires, and never reaches the application.
    /// </para>
    /// </remarks>
    bool IsResumed { get; }

    /// <summary>
    /// Gets a value indicating whether the sender aborted the delivery. Only a resumed delivery reaches the
    /// application aborted, so it can discard what it kept of the delivery from an earlier attachment of the
    /// link (AMQP 1.0 Part 3, section 3.4.6).
    /// </summary>
    /// <remarks>
    /// An aborted delivery is settled and carries no payload (AMQP 1.0 Part 2, section 2.6.14). A delivery that
    /// is not resumed is discarded when the sender aborts it, and never reaches the application.
    /// </remarks>
    bool IsAborted { get; }

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

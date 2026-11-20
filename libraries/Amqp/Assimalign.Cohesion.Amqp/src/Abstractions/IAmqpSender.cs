using System;
using System.Threading;
using System.Threading.Tasks;

namespace Assimalign.Cohesion.Amqp;

/// <summary>
/// Represents the sending endpoint of an attached link: it sends deliveries within the credit the
/// receiver grants (AMQP 1.0 Part 2, section 2.6.7).
/// </summary>
/// <remarks>
/// <para>
/// A delivery waits for link credit, then for room in the session's transfer window, and is split into
/// as many <c>transfer</c> frames as the connection's frame size requires. Sends on one link are
/// serialized, so concurrent calls deliver in the order they acquire the link.
/// </para>
/// <para>
/// An unsettled delivery completes with the receiver's outcome. Under
/// <see cref="AmqpReceiverSettleMode.Second"/> the sender settles the delivery when that outcome
/// arrives, and the receiver settles after it.
/// </para>
/// </remarks>
public interface IAmqpSender : IAmqpLink
{
    /// <summary>
    /// Gets the link credit the sender has: the number of deliveries it may still send.
    /// </summary>
    uint Credit { get; }

    /// <summary>
    /// Gets a value indicating whether the receiver asked the sender to drain: to use or give back its
    /// credit. With <see cref="AmqpSenderOptions.AutoCompleteDrain"/> disabled, the application sends what
    /// it has and then calls <see cref="CompleteDrain"/>.
    /// </summary>
    bool IsDraining { get; }

    /// <summary>
    /// Waits until the sender has link credit.
    /// </summary>
    /// <param name="cancellationToken">A token to cancel the wait.</param>
    /// <returns>A task that completes when the sender has credit.</returns>
    /// <exception cref="AmqpException">Thrown with <see cref="AmqpErrorCode.LinkDetached"/> when the link detaches first.</exception>
    /// <exception cref="OperationCanceledException">Thrown when the wait is canceled.</exception>
    ValueTask WaitForCreditAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Ends a drain the receiver asked for: gives back the credit the sender has not used, so the
    /// receiver's credit drops to zero.
    /// </summary>
    /// <remarks>Calling it when no drain is pending does nothing.</remarks>
    void CompleteDrain();

    /// <summary>
    /// Encodes a message and sends it as one delivery.
    /// </summary>
    /// <param name="message">The message to send.</param>
    /// <param name="options">The delivery's tag, format and settlement; <see langword="null"/> uses the link's defaults.</param>
    /// <param name="cancellationToken">A token to cancel the send. A delivery canceled after its first frame is sent is aborted.</param>
    /// <returns>
    /// The receiver's outcome for an unsettled delivery, or <see langword="null"/> for a delivery sent
    /// settled or settled by the receiver without one.
    /// </returns>
    /// <exception cref="ArgumentNullException">Thrown when <paramref name="message"/> is <see langword="null"/>.</exception>
    /// <exception cref="AmqpException">Thrown with <see cref="AmqpErrorCode.MessageSizeExceeded"/> when the encoded message exceeds <see cref="IAmqpLink.RemoteMaxMessageSize"/>, or with <see cref="AmqpErrorCode.LinkDetached"/> when the link detaches first.</exception>
    /// <exception cref="OperationCanceledException">Thrown when the send is canceled.</exception>
    ValueTask<AmqpDeliveryState?> SendAsync(AmqpMessage message, AmqpDeliveryOptions? options = null, CancellationToken cancellationToken = default);

    /// <summary>
    /// Sends an encoded payload as one delivery, without decoding it, such as a stored message a broker forwards.
    /// </summary>
    /// <param name="payload">The delivery's payload, normally an encoded message. It must not change until the call completes.</param>
    /// <param name="options">The delivery's tag, format and settlement; <see langword="null"/> uses the link's defaults.</param>
    /// <param name="cancellationToken">A token to cancel the send. A delivery canceled after its first frame is sent is aborted.</param>
    /// <returns>
    /// The receiver's outcome for an unsettled delivery, or <see langword="null"/> for a delivery sent
    /// settled or settled by the receiver without one.
    /// </returns>
    /// <exception cref="AmqpException">Thrown with <see cref="AmqpErrorCode.MessageSizeExceeded"/> when the payload exceeds <see cref="IAmqpLink.RemoteMaxMessageSize"/>, or with <see cref="AmqpErrorCode.LinkDetached"/> when the link detaches first.</exception>
    /// <exception cref="OperationCanceledException">Thrown when the send is canceled.</exception>
    ValueTask<AmqpDeliveryState?> SendAsync(ReadOnlyMemory<byte> payload, AmqpDeliveryOptions? options = null, CancellationToken cancellationToken = default);
}

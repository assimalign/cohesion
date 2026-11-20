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
    /// Gets the transaction the receiver last named for the deliveries the sender acquires, or
    /// <see langword="null"/> when it named none or the transaction was cleared (AMQP 1.0 Part 4, section 4.4.3).
    /// </summary>
    /// <remarks>
    /// <para>
    /// A receiver names it in the <c>txn-id</c> entry of a <c>flow</c>'s properties, which only a sender with
    /// <see cref="AmqpSenderOptions.TransactionalAcquisition"/> accepts. A later <c>flow</c> naming another
    /// transaction replaces it; one that names none leaves it, because only a discharge clears it.
    /// </para>
    /// <para>
    /// The sender does not stamp deliveries itself: the application decides which transaction a message it
    /// acquires belongs to, and sends it with an <see cref="AmqpTransactionalState"/> naming that transaction
    /// in <see cref="AmqpDeliveryOptions.State"/>.
    /// </para>
    /// </remarks>
    ReadOnlyMemory<byte>? AcquisitionTransactionId { get; }

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
    /// Clears the transaction the receiver named for acquisitions, once that transaction is discharged
    /// (AMQP 1.0 Part 4, section 4.4.3).
    /// </summary>
    /// <param name="transactionId">The identifier of the discharged transaction.</param>
    /// <returns>
    /// <see langword="true"/> when <see cref="AcquisitionTransactionId"/> named the transaction and is now
    /// cleared; <see langword="false"/> when it named another transaction or none, and is unchanged.
    /// </returns>
    /// <remarks>
    /// The engine does not see discharges, which the coordinator handles; the application that discharges a
    /// transaction clears it from every sender that acquires under it. A transaction a receiver names again
    /// after its discharge is unknown, which the application refuses with
    /// <see cref="AmqpErrorCondition.TransactionUnknownId"/>.
    /// </remarks>
    bool ClearAcquisitionTransaction(ReadOnlyMemory<byte> transactionId);

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
    /// <exception cref="ArgumentException">Thrown when a value in the message has no AMQP representation or nests too deeply, or an application property holds a list, map or array, or when the options break a rule of the link (see <see cref="SendAsync(ReadOnlyMemory{byte}, AmqpDeliveryOptions?, CancellationToken)"/>). Nothing is sent.</exception>
    /// <exception cref="InvalidOperationException">Thrown when the delivery is new and either unsettled map of the link's attach was incomplete (<see cref="AmqpUnsettledReconciliation.IsComplete"/>). Nothing is sent.</exception>
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
    /// <remarks>
    /// A resumed delivery (<see cref="AmqpDeliveryOptions.Resume"/>) may carry an empty payload when the
    /// receiver already has the whole message and only the sender's terminal state or settlement is left to
    /// convey (AMQP 1.0 Part 2, section 2.7.5).
    /// </remarks>
    /// <exception cref="ArgumentException">
    /// Thrown, before anything is sent, when the options break a rule of the link: a settlement the link's
    /// sender settlement mode rules out; a settled or transactional delivery on a link to a transaction
    /// coordinator; a resumed delivery without a tag, or whose tag
    /// <see cref="IAmqpLink.UnsettledReconciliation"/> does not list as resumable or that was already resumed;
    /// a new delivery under the tag of one the receiver waits to have resumed; an <see cref="AmqpReceived"/>
    /// state on a delivery that is not resumed; or a delivery state too large to leave room for payload in a
    /// frame.
    /// </exception>
    /// <exception cref="InvalidOperationException">Thrown when the delivery is new and either unsettled map of the link's attach was incomplete (<see cref="AmqpUnsettledReconciliation.IsComplete"/>): only resumed deliveries can be sent until the link attaches again with complete maps (AMQP 1.0 Part 2, section 2.6.13). Nothing is sent.</exception>
    /// <exception cref="AmqpException">Thrown with <see cref="AmqpErrorCode.MessageSizeExceeded"/> when the payload exceeds <see cref="IAmqpLink.RemoteMaxMessageSize"/>, or with <see cref="AmqpErrorCode.LinkDetached"/> when the link detaches first.</exception>
    /// <exception cref="OperationCanceledException">Thrown when the send is canceled.</exception>
    ValueTask<AmqpDeliveryState?> SendAsync(ReadOnlyMemory<byte> payload, AmqpDeliveryOptions? options = null, CancellationToken cancellationToken = default);

    /// <summary>
    /// Aborts a delivery carried over from an earlier attachment of the link: sends one resuming
    /// <c>transfer</c> with the aborted flag, so the receiver discards what it holds of the delivery and both
    /// ends consider it settled (AMQP 1.0 Part 3, section 3.4.6).
    /// </summary>
    /// <param name="deliveryTag">The tag of a delivery <see cref="IAmqpLink.UnsettledReconciliation"/> lists as resumable, which has not been resumed or aborted yet.</param>
    /// <param name="cancellationToken">A token to cancel the wait for link credit and the session's window.</param>
    /// <returns>A task that completes once the transfer is queued.</returns>
    /// <remarks>
    /// A sender aborts a delivery it cannot resume to completion: the receiver lacks data the sender can no
    /// longer resend, or the sender reached a terminal outcome before the receiver had the whole message. It may
    /// then send the message again as a new delivery. The transfer takes a unit of link credit, like any
    /// delivery.
    /// </remarks>
    /// <exception cref="ArgumentException">Thrown when <see cref="IAmqpLink.UnsettledReconciliation"/> does not list the delivery as resumable, or it was already resumed or aborted. Nothing is sent.</exception>
    /// <exception cref="AmqpException">Thrown with <see cref="AmqpErrorCode.LinkDetached"/> when the link detaches first.</exception>
    /// <exception cref="OperationCanceledException">Thrown when the wait is canceled; nothing was sent.</exception>
    ValueTask AbortDeliveryAsync(AmqpDeliveryTag deliveryTag, CancellationToken cancellationToken = default);
}

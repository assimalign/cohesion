using System;
using System.Diagnostics.CodeAnalysis;
using System.Threading;
using System.Threading.Tasks;

namespace Assimalign.Cohesion.Amqp;

/// <summary>
/// Represents the receiving endpoint of an attached link: it grants credit and receives deliveries
/// (AMQP 1.0 Part 2, section 2.6.7).
/// </summary>
/// <remarks>
/// With a positive <see cref="AmqpReceiverOptions.Prefetch"/>, the receiver keeps that many deliveries
/// granted or buffered, topping credit up as the application takes deliveries. With a prefetch of zero,
/// the application grants credit itself with <see cref="AddCredit(uint)"/>.
/// </remarks>
public interface IAmqpReceiver : IAmqpLink
{
    /// <summary>
    /// Gets the link credit the receiver has granted and the sender has not used.
    /// </summary>
    uint Credit { get; }

    /// <summary>
    /// Gets the state of the delivery the receiver is in the middle of receiving: one whose first
    /// <c>transfer</c> frame has arrived and whose last has not. <see langword="null"/> when there is none, or
    /// when it carries no state.
    /// </summary>
    /// <remarks>
    /// A transactional resource reads it before it commits a transaction: discharging a transaction against
    /// which a delivery is partly posted is an error that terminates the control link with
    /// <see cref="AmqpErrorCondition.TransactionRollback"/> (AMQP 1.0 Part 4, section 4.4.1).
    /// </remarks>
    AmqpDeliveryState? PartialDeliveryState { get; }

    /// <summary>
    /// Returns the next complete delivery, waiting for one when none is buffered.
    /// </summary>
    /// <param name="cancellationToken">A token to cancel the wait.</param>
    /// <returns>The next delivery.</returns>
    /// <exception cref="AmqpException">Thrown with <see cref="AmqpErrorCode.LinkDetached"/> once the link has detached and no delivery is buffered.</exception>
    /// <exception cref="OperationCanceledException">Thrown when the wait is canceled.</exception>
    ValueTask<IAmqpDelivery> ReceiveAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Returns the next complete delivery when one is buffered, without waiting.
    /// </summary>
    /// <param name="delivery">The next delivery, or <see langword="null"/>.</param>
    /// <returns><see langword="true"/> when a delivery was buffered; otherwise <see langword="false"/>.</returns>
    bool TryReceive([NotNullWhen(true)] out IAmqpDelivery? delivery);

    /// <summary>
    /// Grants the sender more link credit.
    /// </summary>
    /// <param name="credit">The number of additional deliveries the sender may send.</param>
    /// <exception cref="AmqpException">Thrown with <see cref="AmqpErrorCode.LinkDetached"/> when the link has detached.</exception>
    void AddCredit(uint credit);

    /// <summary>
    /// Names the transaction the sender acquires deliveries under, or stops naming one (AMQP 1.0 Part 4,
    /// section 4.4.3).
    /// </summary>
    /// <param name="transactionId">
    /// The transaction to name in the <c>txn-id</c> entry of the properties of this and every later
    /// <c>flow</c>, or <see langword="null"/> to name none.
    /// </param>
    /// <remarks>
    /// <para>
    /// The receiver sends a <c>flow</c> at once. A sender that does not support transactional acquisition
    /// detaches the link with <see cref="AmqpErrorCondition.NotImplemented"/>. One that does sends each
    /// delivery it acquires with an <see cref="AmqpTransactionalState"/> naming the transaction, and the
    /// application settles it with a transactional outcome, which applies when the transaction commits.
    /// </para>
    /// <para>
    /// Section 4.4.3 asks the controller to leave the sender no credit before it discharges the transaction,
    /// so nothing is acquired outside it: drain with <see cref="DrainAsync(CancellationToken)"/>, stop naming
    /// the transaction, then discharge it.
    /// </para>
    /// </remarks>
    /// <exception cref="InvalidOperationException">Thrown when the link's target is a transaction coordinator: the control link carries no transactional work (section 4.1).</exception>
    /// <exception cref="AmqpException">Thrown with <see cref="AmqpErrorCode.LinkDetached"/> when the link has detached.</exception>
    void SetAcquisitionTransaction(ReadOnlyMemory<byte>? transactionId);

    /// <summary>
    /// Asks the sender to drain: to send what it has within the granted credit and give back the rest,
    /// and waits until the receiver's credit is zero.
    /// </summary>
    /// <param name="cancellationToken">A token to cancel the wait.</param>
    /// <returns>A task that completes when the link has no credit left.</returns>
    /// <exception cref="AmqpException">Thrown with <see cref="AmqpErrorCode.LinkDetached"/> when the link detaches first.</exception>
    /// <exception cref="OperationCanceledException">Thrown when the wait is canceled.</exception>
    ValueTask DrainAsync(CancellationToken cancellationToken = default);
}

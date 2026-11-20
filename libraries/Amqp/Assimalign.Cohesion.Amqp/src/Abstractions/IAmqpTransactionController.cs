using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace Assimalign.Cohesion.Amqp;

/// <summary>
/// Represents the client side of a transaction coordinator link: it declares transactions and discharges
/// them (AMQP 1.0 Part 4, section 4.3).
/// </summary>
/// <remarks>
/// Work joins a transaction through its identifier: a send carries an <see cref="AmqpTransactionalState"/> in
/// <see cref="AmqpDeliveryOptions.State"/>, and a settlement passes one to
/// <see cref="IAmqpDelivery.SettleAsync(AmqpDeliveryState?, CancellationToken)"/>.
/// </remarks>
public interface IAmqpTransactionController : IAsyncDisposable
{
    /// <summary>
    /// Gets the coordinator link the controller sends on.
    /// </summary>
    IAmqpSender Link { get; }

    /// <summary>
    /// Gets the capabilities the coordinator announced in its <c>attach</c>, or <see langword="null"/> when it
    /// announced none (AMQP 1.0 Part 4, section 4.5.1).
    /// </summary>
    IReadOnlyList<AmqpSymbol>? Capabilities { get; }

    /// <summary>
    /// Declares a local transaction.
    /// </summary>
    /// <param name="cancellationToken">A token to cancel the wait for the coordinator's answer.</param>
    /// <returns>The identifier the coordinator allocated.</returns>
    /// <exception cref="AmqpException">Thrown with <see cref="AmqpErrorCode.TransactionFailed"/> when the coordinator refuses the declare, or with <see cref="AmqpErrorCode.LinkDetached"/> when the coordinator link detaches first.</exception>
    ValueTask<ReadOnlyMemory<byte>> DeclareAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Discharges a transaction: commits its work, or rolls it back.
    /// </summary>
    /// <param name="transactionId">The identifier of the transaction.</param>
    /// <param name="fail"><see langword="true"/> to roll the transaction back; <see langword="false"/> to commit it.</param>
    /// <param name="cancellationToken">A token to cancel the wait for the coordinator's answer.</param>
    /// <returns>A task that completes when the coordinator accepted the discharge.</returns>
    /// <exception cref="AmqpException">Thrown with <see cref="AmqpErrorCode.TransactionFailed"/> when the coordinator refuses the discharge, such as with <see cref="AmqpErrorCondition.TransactionRollback"/> for a commit it could not complete.</exception>
    ValueTask DischargeAsync(ReadOnlyMemory<byte> transactionId, bool fail = false, CancellationToken cancellationToken = default);
}

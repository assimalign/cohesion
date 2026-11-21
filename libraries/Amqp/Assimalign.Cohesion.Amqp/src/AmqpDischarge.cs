using System;

namespace Assimalign.Cohesion.Amqp;

/// <summary>
/// Represents the <c>discharge</c> message body: a request to the transaction coordinator to end a
/// transaction, committing or rolling back its work (AMQP 1.0 Part 4, section 4.5.3).
/// </summary>
/// <remarks>
/// It travels as the value of an <see cref="AmqpValueBody"/> on a coordinator link, and the coordinator
/// answers with <see cref="AmqpAccepted"/>, or with an <see cref="AmqpRejected"/> outcome that carries a
/// transaction error such as <see cref="AmqpErrorCondition.TransactionRollback"/>.
/// </remarks>
public sealed class AmqpDischarge
{
    /// <summary>
    /// Initializes a new discharge.
    /// </summary>
    /// <param name="transactionId">The identifier of the transaction to discharge.</param>
    /// <param name="fail"><see langword="true"/> to roll the transaction back; <see langword="false"/> to commit it.</param>
    public AmqpDischarge(ReadOnlyMemory<byte> transactionId, bool fail = false)
    {
        TransactionId = transactionId;
        Fail = fail;
    }

    /// <summary>
    /// Gets the identifier of the transaction to discharge.
    /// </summary>
    public ReadOnlyMemory<byte> TransactionId { get; }

    /// <summary>
    /// Gets a value indicating whether the transaction is rolled back rather than committed.
    /// </summary>
    public bool Fail { get; }
}

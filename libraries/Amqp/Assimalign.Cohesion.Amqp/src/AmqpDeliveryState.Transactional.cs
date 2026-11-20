using System;

namespace Assimalign.Cohesion.Amqp;

/// <summary>
/// Represents the <c>transactional-state</c> delivery state: the delivery's transfer or outcome is part of
/// a transaction, and takes effect only when the transaction is discharged successfully (AMQP 1.0 Part 4,
/// section 4.5.5).
/// </summary>
/// <remarks>
/// A sender puts work under a transaction by sending the delivery with this state
/// (<see cref="AmqpDeliveryOptions.State"/>); a receiver does so by settling with it, carrying the outcome
/// that applies when the transaction commits.
/// </remarks>
public sealed class AmqpTransactionalState : AmqpDeliveryState
{
    /// <summary>
    /// Initializes a new transactional state.
    /// </summary>
    /// <param name="transactionId">The identifier of the transaction the work belongs to.</param>
    /// <param name="outcome">The outcome that applies when the transaction commits, or <see langword="null"/>.</param>
    public AmqpTransactionalState(ReadOnlyMemory<byte> transactionId, AmqpOutcome? outcome = null)
    {
        TransactionId = transactionId;
        Outcome = outcome;
    }

    /// <summary>
    /// Gets the identifier of the transaction the work belongs to.
    /// </summary>
    public ReadOnlyMemory<byte> TransactionId { get; }

    /// <summary>
    /// Gets the outcome that applies when the transaction commits, or <see langword="null"/>.
    /// </summary>
    public AmqpOutcome? Outcome { get; }

    /// <summary>
    /// Returns the state's name, transaction identifier and outcome.
    /// </summary>
    /// <returns>The state's name, the identifier in hexadecimal, and the outcome when there is one.</returns>
    public override string ToString()
    {
        string transactionId = Convert.ToHexStringLower(TransactionId.Span);

        return Outcome is null
            ? "transactional-state (" + transactionId + ")"
            : "transactional-state (" + transactionId + ", " + Outcome + ")";
    }
}

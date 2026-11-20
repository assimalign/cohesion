using System;

namespace Assimalign.Cohesion.Amqp;

/// <summary>
/// Represents the <c>declared</c> outcome: a transaction coordinator accepted a declare and allocated a
/// transaction (AMQP 1.0 Part 4, section 4.5.4).
/// </summary>
public sealed class AmqpDeclared : AmqpOutcome
{
    /// <summary>
    /// Initializes a new declared outcome.
    /// </summary>
    /// <param name="transactionId">The identifier the coordinator allocated to the transaction.</param>
    public AmqpDeclared(ReadOnlyMemory<byte> transactionId)
    {
        TransactionId = transactionId;
    }

    /// <summary>
    /// Gets the identifier the coordinator allocated to the transaction.
    /// </summary>
    public ReadOnlyMemory<byte> TransactionId { get; }

    /// <summary>
    /// Returns the outcome's name and transaction identifier.
    /// </summary>
    /// <returns>The outcome's name and the identifier in hexadecimal.</returns>
    public override string ToString()
    {
        return "declared (" + Convert.ToHexStringLower(TransactionId.Span) + ")";
    }
}

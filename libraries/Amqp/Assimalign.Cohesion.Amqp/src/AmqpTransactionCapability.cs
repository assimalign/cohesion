namespace Assimalign.Cohesion.Amqp;

/// <summary>
/// Provides the transaction capabilities AMQP 1.0 defines, which a <see cref="AmqpCoordinator"/> carries
/// (Part 4, section 4.5.7).
/// </summary>
public static class AmqpTransactionCapability
{
    /// <summary>
    /// Gets <c>amqp:local-transactions</c>: the coordinator supports local transactions.
    /// </summary>
    public static AmqpSymbol LocalTransactions { get; } = new("amqp:local-transactions");

    /// <summary>
    /// Gets <c>amqp:distributed-transactions</c>: the coordinator supports AMQP distributed transactions.
    /// </summary>
    public static AmqpSymbol DistributedTransactions { get; } = new("amqp:distributed-transactions");

    /// <summary>
    /// Gets <c>amqp:promotable-transactions</c>: the coordinator supports promotable transactions.
    /// </summary>
    public static AmqpSymbol PromotableTransactions { get; } = new("amqp:promotable-transactions");

    /// <summary>
    /// Gets <c>amqp:multi-txns-per-ssn</c>: more than one transaction may be active on a session at once.
    /// </summary>
    public static AmqpSymbol MultipleTransactionsPerSession { get; } = new("amqp:multi-txns-per-ssn");

    /// <summary>
    /// Gets <c>amqp:multi-ssns-per-txn</c>: a transaction may span more than one session.
    /// </summary>
    public static AmqpSymbol MultipleSessionsPerTransaction { get; } = new("amqp:multi-ssns-per-txn");
}

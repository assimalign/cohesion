namespace Assimalign.Cohesion.Database.Transactions;

/// <summary>
/// The engine-internal state of one in-flight transaction: its identity, sequence,
/// isolation level, and visibility snapshot.
/// </summary>
/// <remarks>
/// Execution operators carry this context to storage, index, and catalog operations
/// so every read resolves through the same snapshot and every write is stamped with
/// the same sequence. It is the engine-side counterpart of the public
/// <see cref="IDatabaseTransaction"/> surface.
/// </remarks>
public interface ITransactionContext
{
    /// <summary>
    /// Gets the external identity of the transaction.
    /// </summary>
    TransactionId Id { get; }

    /// <summary>
    /// Gets the MVCC ordering sequence assigned to the transaction.
    /// </summary>
    TransactionSequence Sequence { get; }

    /// <summary>
    /// Gets the isolation level the transaction runs under.
    /// </summary>
    IsolationLevel IsolationLevel { get; }

    /// <summary>
    /// Gets the current state of the transaction.
    /// </summary>
    TransactionState State { get; }

    /// <summary>
    /// Gets the visibility snapshot reads resolve through. Under
    /// <see cref="IsolationLevel.ReadCommitted"/> the snapshot is refreshed per
    /// statement; under snapshot and serializable isolation it is fixed at begin.
    /// </summary>
    TransactionSnapshot Snapshot { get; }
}

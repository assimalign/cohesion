using Assimalign.Cohesion.Database.Transactions;

namespace Assimalign.Cohesion.Database.KeyValuePair.Internal;

/// <summary>
/// The per-command execution context: the MVCC transaction the command runs
/// under, the visibility snapshot captured once at command start, and the
/// database's transaction coordinator (the key lock in phase one, the gated
/// apply bracket in phase two). Under <c>IsolationLevel.ReadCommitted</c> the
/// context's snapshot re-captures per access, so capturing here is what gives
/// each command exactly one refreshed view; under <c>Snapshot</c> isolation the
/// same capture returns the begin-time snapshot for every command.
/// </summary>
internal readonly struct KeyValueStatementContext
{
    internal KeyValueStatementContext(ITransactionContext transaction, KeyValueTransactionCoordinator coordinator)
    {
        Transaction = transaction;
        Coordinator = coordinator;
        Snapshot = transaction.Snapshot;
    }

    /// <summary>
    /// Gets the MVCC transaction context the command executes under.
    /// </summary>
    internal ITransactionContext Transaction { get; }

    /// <summary>
    /// Gets the database's transaction coordinator.
    /// </summary>
    internal KeyValueTransactionCoordinator Coordinator { get; }

    /// <summary>
    /// Gets the visibility snapshot for the whole command.
    /// </summary>
    internal TransactionSnapshot Snapshot { get; }
}

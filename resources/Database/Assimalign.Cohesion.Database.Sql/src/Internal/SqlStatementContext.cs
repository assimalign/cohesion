using Assimalign.Cohesion.Database.Storage;
using Assimalign.Cohesion.Database.Transactions;

namespace Assimalign.Cohesion.Database.Sql.Internal;

/// <summary>
/// The per-statement execution context: the MVCC transaction the statement runs
/// under, its paired storage bracket, and the visibility snapshot captured once
/// at statement start — under <c>IsolationLevel.ReadCommitted</c> the context's
/// snapshot re-captures per access, so capturing here is what gives each
/// statement exactly one refreshed view; under <c>Snapshot</c> isolation the
/// same capture returns the begin-time snapshot for every statement.
/// </summary>
internal readonly struct SqlStatementContext
{
    internal SqlStatementContext(ITransactionContext transaction, IStorageTransaction storageTransaction)
    {
        Transaction = transaction;
        StorageTransaction = storageTransaction;
        Snapshot = transaction.Snapshot;
    }

    /// <summary>
    /// Gets the MVCC transaction context the statement executes under.
    /// </summary>
    internal ITransactionContext Transaction { get; }

    /// <summary>
    /// Gets the storage bracket the statement's page mutations ride.
    /// </summary>
    internal IStorageTransaction StorageTransaction { get; }

    /// <summary>
    /// Gets the visibility snapshot for the whole statement.
    /// </summary>
    internal TransactionSnapshot Snapshot { get; }
}

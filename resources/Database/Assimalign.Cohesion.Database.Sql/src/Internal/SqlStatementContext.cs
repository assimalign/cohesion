using Assimalign.Cohesion.Database.Transactions;

namespace Assimalign.Cohesion.Database.Sql.Internal;

/// <summary>
/// The per-statement execution context: the MVCC transaction the statement runs
/// under, the visibility snapshot captured once at statement start, and the
/// database's transaction coordinator (row locks in phase one, the gated apply
/// bracket in phase two). Under <c>IsolationLevel.ReadCommitted</c> the
/// context's snapshot re-captures per access, so capturing here is what gives
/// each statement exactly one refreshed view; under <c>Snapshot</c> isolation
/// the same capture returns the begin-time snapshot for every statement.
/// </summary>
internal readonly struct SqlStatementContext
{
    internal SqlStatementContext(ITransactionContext transaction, SqlTransactionCoordinator coordinator)
    {
        Transaction = transaction;
        Coordinator = coordinator;
        Snapshot = transaction.Snapshot;
        Metrics = new SqlStatementMetrics();
    }

    /// <summary>
    /// Gets the MVCC transaction context the statement executes under.
    /// </summary>
    internal ITransactionContext Transaction { get; }

    /// <summary>
    /// Gets the database's transaction coordinator.
    /// </summary>
    internal SqlTransactionCoordinator Coordinator { get; }

    /// <summary>
    /// Gets the visibility snapshot for the whole statement.
    /// </summary>
    internal TransactionSnapshot Snapshot { get; }

    /// <summary>
    /// Gets the statement's execution observability (access path, records
    /// examined) — the session exposes the last statement's instance to tests.
    /// </summary>
    internal SqlStatementMetrics Metrics { get; }
}

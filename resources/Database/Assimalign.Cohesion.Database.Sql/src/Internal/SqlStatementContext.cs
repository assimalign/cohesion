using Assimalign.Cohesion.Database.Sql.Catalog;
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
    internal SqlStatementContext(
        ITransactionContext transaction,
        TransactionCoordinator coordinator,
        string? provisioningSchema = null,
        string databaseName = "",
        SqlCatalogSnapshot? catalogSnapshot = null)
    {
        Transaction = transaction;
        Coordinator = coordinator;
        Snapshot = transaction.Snapshot;
        Metrics = new SqlStatementMetrics();
        ProvisioningSchema = provisioningSchema;
        DatabaseName = databaseName;
        CatalogSnapshot = catalogSnapshot ?? SqlCatalogSnapshot.Empty;
    }

    /// <summary>
    /// Gets the MVCC transaction context the statement executes under.
    /// </summary>
    internal ITransactionContext Transaction { get; }

    /// <summary>
    /// Gets the database's transaction coordinator.
    /// </summary>
    internal TransactionCoordinator Coordinator { get; }

    /// <summary>
    /// Gets the visibility snapshot for the whole statement.
    /// </summary>
    internal TransactionSnapshot Snapshot { get; }

    /// <summary>
    /// Gets the statement's execution observability (access path, records
    /// examined) — the session exposes the last statement's instance to tests.
    /// </summary>
    internal SqlStatementMetrics Metrics { get; }

    /// <summary>Gets the applying compiled schema's name, or null for a live-session statement.</summary>
    internal string? ProvisioningSchema { get; }

    /// <summary>Gets the current database's catalog identifier for metadata rows.</summary>
    internal string DatabaseName { get; }

    /// <summary>Gets the catalog directory paired with this statement's visibility lifetime.</summary>
    internal SqlCatalogSnapshot CatalogSnapshot { get; }
}

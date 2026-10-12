using System;

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
        TransactionContext transaction,
        TransactionCoordinator coordinator,
        string? provisioningSchema = null,
        string databaseName = "",
        SqlCatalogSnapshot? catalogSnapshot = null,
        TransactionSequence? ddlLockOwner = null,
        Action<long, long?>? provisioningProgress = null,
        SqlCatalogScript? scriptReceipt = null)
    {
        Transaction = transaction;
        Coordinator = coordinator;
        Snapshot = transaction.Snapshot;
        Metrics = new SqlStatementMetrics();
        ProvisioningSchema = provisioningSchema;
        DatabaseName = databaseName;
        CatalogSnapshot = catalogSnapshot;
        DdlLockOwner = ddlLockOwner ?? transaction.Sequence;
        ProvisioningProgress = provisioningProgress;
        ScriptReceipt = scriptReceipt;
    }

    /// <summary>
    /// Gets the MVCC transaction context the statement executes under.
    /// </summary>
    internal TransactionContext Transaction { get; }

    // Schema steps keep their table locks until the entire apply and compensation end,
    // while each statement's data/catalog brackets retain their existing commit lifetime.
    internal TransactionSequence DdlLockOwner { get; }

    internal Action<long, long?>? ProvisioningProgress { get; }

    /// <summary>Gets the receipt published only by this statement's explicit commit.</summary>
    internal SqlCatalogScript? ScriptReceipt { get; }

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
    internal SqlCatalogSnapshot? CatalogSnapshot { get; }
}

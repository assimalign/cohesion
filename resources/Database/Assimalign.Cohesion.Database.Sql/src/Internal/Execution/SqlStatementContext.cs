using System;
using System.Threading;
using System.Threading.Tasks;

using Assimalign.Cohesion.Database.Execution;
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
[global::System.Diagnostics.DebuggerDisplay("SqlStatementContext")]
[global::System.Diagnostics.DebuggerTypeProxy(typeof(global::Assimalign.Cohesion.Database.Sql.Internal.SqlStatementContext.DebugView))]
internal readonly struct SqlStatementContext
{
    private readonly long _transactionStartTicks;
    // Normal statements carry only their database name. Provisioning needs both names and
    // uses a shared immutable pair, leaving room for the clock without growing normal contexts.
    private readonly object _names;

    internal SqlStatementContext(
        TransactionContext transaction,
        TransactionCoordinator coordinator,
        string? provisioningSchema = null,
        string databaseName = "",
        SqlCatalogSnapshot? catalogSnapshot = null,
        TransactionSequence? ddlLockOwner = null,
        Action<long, long?>? provisioningProgress = null,
        SqlCatalogScriptPublication? scriptReceipt = null,
        DateTimeOffset? transactionStart = null,
        SqlDdlInterlock? ddlInterlock = null,
        SqlResultPreparation? resultPreparation = null,
        TimeSpan metadataLockTimeout = default)
    {
        _transactionStartTicks = (transactionStart ?? DateTimeOffset.UtcNow).UtcTicks;
        _names = provisioningSchema is null && metadataLockTimeout == TimeSpan.Zero ? databaseName
            : new ProvisioningNames(provisioningSchema, databaseName, metadataLockTimeout);
        Transaction = transaction;
        Coordinator = coordinator;
        Snapshot = transaction.Snapshot;
        Metrics = new SqlStatementMetrics();
        Metrics.CatalogSnapshot = catalogSnapshot;
        DdlLockOwner = ddlLockOwner ?? transaction.Sequence;
        ProvisioningProgress = provisioningProgress;
        ScriptReceipt = scriptReceipt;
        DdlInterlock = ddlInterlock;
        Metrics.ResultPreparation = resultPreparation;
    }

    /// <summary>
    /// Gets the MVCC transaction context the statement executes under.
    /// </summary>
    internal TransactionContext Transaction { get; }

    internal DateTimeOffset TransactionStart => new(_transactionStartTicks, TimeSpan.Zero);

    // Schema steps keep their table locks until the entire apply and compensation end,
    // while each statement's data/catalog brackets retain their existing commit lifetime.
    internal TransactionSequence DdlLockOwner { get; }

    internal Action<long, long?>? ProvisioningProgress { get; }

    /// <summary>Gets the receipt published only by this statement's explicit commit.</summary>
    internal SqlCatalogScriptPublication? ScriptReceipt { get; }

    /// <summary>The session's metadata admission; direct executor probes may omit it.</summary>
    internal SqlDdlInterlock? DdlInterlock { get; }

    internal TimeSpan MetadataLockTimeout => (_names as ProvisioningNames)?.LockTimeout ?? TimeSpan.Zero;

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

    internal SqlStatementReferences References => Metrics.References ??= new SqlStatementReferences();

    /// <summary>Gets the applying compiled schema's name, or null for a live-session statement.</summary>
    internal string? ProvisioningSchema => (_names as ProvisioningNames)?.Schema;

    /// <summary>Gets the current database's catalog identifier for metadata rows.</summary>
    internal string DatabaseName => _names is string name ? name : ((ProvisioningNames)_names).Database;

    /// <summary>Gets the catalog directory paired with this statement's visibility lifetime.</summary>
    internal SqlCatalogSnapshot? CatalogSnapshot => Metrics.CatalogSnapshot;

    /// <summary>Requires binding to have supplied the catalog image before execution, including empty inputs.</summary>
    internal SqlCatalogSnapshot RequireCatalogSnapshot()
        => CatalogSnapshot ?? throw new DatabaseException(ErrorMessages.ContextRequireCatalogSnapshotDatabase);

    /// <summary>Captures lazily in the existing shared statement state; context copies use the same image.</summary>
    internal void CaptureCatalogSnapshot(SqlCatalog catalog) => Metrics.CaptureCatalogSnapshot(catalog);

    // DML calls this before its physical statement bracket can commit.
    internal ValueTask<DatabaseResult> PrepareResultAsync(DatabaseResult result, CancellationToken cancellationToken)
        => Metrics.ResultPreparation is { } preparation ? preparation.PrepareAsync(result, cancellationToken) : ValueTask.FromResult(result);

    [global::System.Diagnostics.DebuggerDisplay("ProvisioningNames")]
    [global::System.Diagnostics.DebuggerTypeProxy(typeof(global::Assimalign.Cohesion.Database.Sql.Internal.SqlStatementContext.ProvisioningNames.DebugView))]
    private sealed class ProvisioningNames
    {
        internal ProvisioningNames(string? schema, string database, TimeSpan lockTimeout)
        {
            Schema = schema;
            Database = database;
            LockTimeout = lockTimeout;
        }

        internal string? Schema { get; }
        internal string Database { get; }
        internal TimeSpan LockTimeout { get; }

        private sealed class DebugView
        {
            public DebugView(global::Assimalign.Cohesion.Database.Sql.Internal.SqlStatementContext.ProvisioningNames value)
            {
                _ = value;
            }
        }
    }

    private sealed class DebugView
    {
        public DebugView(global::Assimalign.Cohesion.Database.Sql.Internal.SqlStatementContext value)
        {
            _ = value;
        }
    }
}

using System;
using System.Threading;

namespace Assimalign.Cohesion.Database.Sql.Internal;

/// <summary>
/// The engine-owned MVCC version-purge worker — a documented stub in the current
/// SQL engine cut.
/// </summary>
/// <remarks>
/// The SQL engine currently serializes at page grain through storage transactions
/// and holds no <c>IVersionStore</c>, so there are no version chains to purge (see
/// docs/DESIGN.md non-goals). The worker exists so the engine's worker inventory —
/// and any host mapping built on it — is stable: when the shared
/// <c>Database.Transactions</c> MVCC manager integrates with engine sessions, the
/// purge body (drive <c>IVersionStore.PurgeWriterAsync</c> plus the oldest-active
/// prune bound) fills in here without changing the seam or any host.
/// </remarks>
internal sealed class SqlVersionPurgeWorker : DatabaseEngineWorker
{
    private readonly SqlDatabaseEngine _engine;

    internal SqlVersionPurgeWorker(SqlDatabaseEngine engine)
    {
        _engine = engine;
    }

    /// <inheritdoc />
    public override string Name => _engine.Name + "/version-purge";

    /// <inheritdoc />
    public override DatabaseEngineWorkerKind Kind => DatabaseEngineWorkerKind.VersionPurge;

    /// <inheritdoc />
    public override TimeSpan Interval => _engine.EngineOptions.MaintenanceInterval;

    /// <inheritdoc />
    public override void RunIteration(CancellationToken cancellationToken)
    {
        // Stub: no row-level MVCC version store exists in this engine cut.
    }
}

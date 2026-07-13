using System;
using System.Threading;

namespace Assimalign.Cohesion.Database.Sql.Internal;

/// <summary>
/// The engine-owned index-maintenance worker (tombstone vacuum, page merges) — a
/// documented stub in the current SQL engine cut, sanctioned by #902.
/// </summary>
/// <remarks>
/// The B+Tree infrastructure has no compaction yet (tombstone vacuum and page merges
/// are future index-layer features), and the SQL planner does not yet adopt
/// secondary indexes. The worker exists so the engine's worker inventory — and any
/// host mapping built on it — is stable: when index compaction lands, the throttled
/// maintenance body fills in here without changing the seam or any host.
/// </remarks>
internal sealed class SqlIndexMaintenanceWorker : DatabaseEngineWorker
{
    private readonly SqlDatabaseEngine _engine;

    internal SqlIndexMaintenanceWorker(SqlDatabaseEngine engine)
    {
        _engine = engine;
    }

    /// <inheritdoc />
    public override string Name => _engine.Name + "/index-maintenance";

    /// <inheritdoc />
    public override DatabaseEngineWorkerKind Kind => DatabaseEngineWorkerKind.IndexMaintenance;

    /// <inheritdoc />
    public override TimeSpan Interval => _engine.EngineOptions.MaintenanceInterval;

    /// <inheritdoc />
    public override void RunIteration(CancellationToken cancellationToken)
    {
        // Stub: the index layer has no compaction to drive yet.
    }
}

using System;
using System.Threading;

namespace Assimalign.Cohesion.Database.KeyValuePair.Internal;

/// <summary>
/// The engine-owned index-maintenance worker (tombstone vacuum, page merges) — a
/// documented stub, the same sanctioned stub the SQL engine carries.
/// </summary>
/// <remarks>
/// The B+Tree infrastructure has no compaction yet (tombstone vacuum and page
/// merges are future index-layer features). The stub matters <em>more</em> here
/// than in the SQL engine: the primary key index is the key-value model's primary
/// structure, so every delete accrues a tree tombstone. The worker exists so the
/// engine's worker inventory — and any observer over it — is stable: when index
/// compaction lands, the throttled maintenance body fills in here without
/// changing the seam.
/// </remarks>
internal sealed class KeyValueIndexMaintenanceWorker : DatabaseEngineWorker
{
    private readonly KeyValueDatabaseEngine _engine;

    internal KeyValueIndexMaintenanceWorker(KeyValueDatabaseEngine engine)
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

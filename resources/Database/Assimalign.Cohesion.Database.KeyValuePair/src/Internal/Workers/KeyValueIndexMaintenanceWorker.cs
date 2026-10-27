using System;
using System.Collections.Generic;
using System.Threading;

using Assimalign.Cohesion.Database.Storage;

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
[global::System.Diagnostics.DebuggerDisplay("KeyValueIndexMaintenanceWorker {Name,nq}")]
[global::System.Diagnostics.DebuggerTypeProxy(typeof(global::Assimalign.Cohesion.Database.KeyValuePair.Internal.KeyValueIndexMaintenanceWorker.DebugView))]
internal sealed class KeyValueIndexMaintenanceWorker : DatabaseWriteWorker<KeyValueDatabase>
{
    private readonly KeyValueDatabaseEngine _engine;

    internal KeyValueIndexMaintenanceWorker(KeyValueDatabaseEngine engine)
        : base(engine.Name + "/index-maintenance", DatabaseEngineWorkerKind.IndexMaintenance, engine.EngineOptions.MaintenanceInterval)
    {
        _engine = engine;
    }

    protected override KeyValueDatabase[] GetDatabases()
    {
        return _engine.GetInstanceSnapshot();
    }
    protected override string GetName(KeyValueDatabase database) => database.Name;
    protected override bool IsClosed(KeyValueDatabase database) => database.IsClosed;
    protected override bool IsOffline(KeyValueDatabase database) => database.IsOffline;
    protected override bool IsDue(KeyValueDatabase database) => false;
    protected override bool IsOpen(KeyValueDatabase database) => _engine.IsOpen(database);

    protected override DatabaseWriteResult RunDatabase(KeyValueDatabase database, CancellationToken cancellationToken)
    {
        DatabaseWriteResult failures = default;
        // The index layer has no compaction yet; its future body already has database isolation.
        return failures;
    }

    private sealed class DebugView
    {
        private readonly global::Assimalign.Cohesion.Database.KeyValuePair.Internal.KeyValueIndexMaintenanceWorker _target;

        public DebugView(global::Assimalign.Cohesion.Database.KeyValuePair.Internal.KeyValueIndexMaintenanceWorker value)
        {
            _target = value;
        }

        public bool CompletionOnly => _target.CompletionOnly;
        public string Name => _target.Name;
        public global::Assimalign.Cohesion.Database.DatabaseEngineWorkerKind Kind => _target.Kind;
        public global::System.TimeSpan Interval => _target.Interval;
    }
}

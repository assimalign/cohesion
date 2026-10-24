using System;
using System.Collections.Generic;
using System.Threading;

using Assimalign.Cohesion.Database.Storage;

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
[global::System.Diagnostics.DebuggerDisplay("SqlIndexMaintenanceWorker {Name,nq}")]
[global::System.Diagnostics.DebuggerTypeProxy(typeof(global::Assimalign.Cohesion.Database.Sql.Internal.SqlIndexMaintenanceWorker.DebugView))]
internal sealed class SqlIndexMaintenanceWorker : DatabaseWriteWorker<SqlDatabase>
{
    private readonly SqlDatabaseEngine _engine;

    internal SqlIndexMaintenanceWorker(SqlDatabaseEngine engine)
        : base(engine.Name + "/index-maintenance", DatabaseEngineWorkerKind.IndexMaintenance, engine.EngineOptions.MaintenanceInterval)
    {
        _engine = engine;
    }

    protected override SqlDatabase[] GetDatabases()
    {
        return _engine.GetInstanceSnapshot();
    }
    protected override string GetName(SqlDatabase database) => database.Name;
    protected override bool IsClosed(SqlDatabase database) => database.IsClosed;
    protected override bool IsOffline(SqlDatabase database) => database.IsOffline;
    protected override bool IsDue(SqlDatabase database) => false;
    protected override bool IsOpen(SqlDatabase database) => _engine.IsOpen(database);

    protected override DatabaseWriteResult RunDatabase(SqlDatabase database, CancellationToken cancellationToken)
    {
        DatabaseWriteResult failures = default;
        // The index layer has no compaction yet; its future body already has database isolation.
        return failures;
    }

    private sealed class DebugView
    {
        private readonly global::Assimalign.Cohesion.Database.Sql.Internal.SqlIndexMaintenanceWorker _target;

        public DebugView(global::Assimalign.Cohesion.Database.Sql.Internal.SqlIndexMaintenanceWorker value)
        {
            _target = value;
        }

        public global::Assimalign.Cohesion.Database.DatabaseWorkerLanes<global::Assimalign.Cohesion.Database.DatabaseWriteResult> Lanes => _target.Lanes;
        public bool CompletionOnly => _target.CompletionOnly;
        public string Name => _target.Name;
        public global::Assimalign.Cohesion.Database.DatabaseEngineWorkerKind Kind => _target.Kind;
        public global::System.TimeSpan Interval => _target.Interval;
    }
}

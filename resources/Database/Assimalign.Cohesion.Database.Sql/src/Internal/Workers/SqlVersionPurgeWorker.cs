using System;
using System.Threading;

using Assimalign.Cohesion.Database.Transactions;

namespace Assimalign.Cohesion.Database.Sql.Internal;

/// <summary>
/// The engine-owned MVCC version-purge worker: per pass, per open database, it
/// retries the logical undo of any aborted writer whose rollback-time purge
/// failed (<c>VersionStore.PurgeWriterAsync</c>) and physically reclaims
/// versions no snapshot can reach — committed tombstones below the safe prune
/// bound (<c>VersionStore.PruneAsync</c>) — so version-space amplification is
/// bounded by the oldest in-flight snapshot.
/// </summary>
/// <remarks>
/// <para>
/// The full pass runs on the engine's own timer
/// (<see cref="SqlDatabaseEngineOptions.MaintenanceInterval"/>) — embedded and
/// hosted consumers get identical reclamation because nothing outside the
/// engine participates (R10). Passes iterate the engine's database snapshot and
/// tolerate racing a drop. (Aborted writers are normally unlinked inline at rollback,
/// before their locks release — the worker's abort duty is the retry of a failed undo.)
/// </para>
/// <para>
/// <b>Deferred undo runs on its own backoff (#1226).</b> A writer whose undo failed keeps its
/// locks until the undo completes, so its retry cannot wait a maintenance interval: a
/// coordinator that defers an undo wakes the worker, which retries about 100 ms later and then
/// at doubling delays up to the maintenance interval
/// (<c>TransactionCoordinator.NextDeferredUndoRetry</c>).
/// </para>
/// <para>
/// A failure is reported for its database — the engine's observational state flips to Faulted —
/// and the worker keeps running: an undo that fails again is retried at the delay its coordinator
/// sets, and unpurged versions cost space, never consistency. The worker adds no backoff of its own
/// to a database whose undo failed (the coordinator already doubles each retry's delay), and a
/// failure of one database delays no other's retry. A database whose undo is still deferred, or
/// whose storage was busy, keeps a failure recorded for it until a pass leaves nothing over, and
/// one whose full pass failed keeps it until a later full pass completes: a pass that only retries
/// deferred undo does not redo the full pass's work (owner decision 42 review). A
/// failure of another database does not keep it, so a transient fault does not leave the engine
/// Faulted for good. An offline database (#1243) is skipped, and so is a database its holder
/// disposed while the engine keeps it registered (directly, or through a session's database).
/// </para>
/// <para>
/// <b>A failed full pass is retried after the backoff (owner decision 46).</b> A database whose
/// full pass failed has that pass run again for it alone <see cref="DatabaseEngineWorker.FailureBackoff"/>
/// after the failure, not at the next maintenance interval, so a full pass that keeps failing
/// reaches the engine's worker failure window at about the same time as every other worker (about
/// 101 s at the defaults, where waiting for the next interval took 120 s), and one whose fault
/// cleared stops holding its database's failure within a backoff. The other databases keep the
/// interval. The schedule, full passes and retries alike, is kept on the engine's clock, the one
/// its failure window is timed on. A retry runs the coordinator's whole pass, deferred undo
/// included, so while it keeps failing a deferred undo of that database is retried at least once
/// a backoff, not only on its coordinator's doubling schedule.
/// </para>
/// </remarks>
[global::System.Diagnostics.DebuggerDisplay("SqlVersionPurgeWorker {Name,nq}")]
[global::System.Diagnostics.DebuggerTypeProxy(typeof(global::Assimalign.Cohesion.Database.Sql.Internal.SqlVersionPurgeWorker.DebugView))]
internal sealed class SqlVersionPurgeWorker : DatabaseVersionPurgeWorker<SqlDatabase>
{
    private readonly SqlDatabaseEngine _engine;

    internal SqlVersionPurgeWorker(SqlDatabaseEngine engine)
        : base(engine.Name, engine.EngineOptions.MaintenanceInterval, engine.EngineOptions.TimeProvider ?? TimeProvider.System)
    {
        _engine = engine;
    }

    protected override ManualResetEventSlim LaneSignal => _engine.UndoDeferredSignal;
    protected override SqlDatabase[] GetDatabases() => _engine.GetInstanceSnapshot();
    protected override string GetName(SqlDatabase database) => database.Name;
    protected override bool IsClosed(SqlDatabase database) => database.IsClosed;
    protected override bool IsOffline(SqlDatabase database) => database.IsOffline;
    protected override bool IsOpen(SqlDatabase database) => _engine.IsOpen(database);
    protected override TransactionCoordinator GetCoordinator(SqlDatabase database) => database.Coordinator;
    protected override void Purge(SqlDatabase database, CancellationToken cancellationToken)
    {
        try { database.Coordinator.RunVersionPurgePass(cancellationToken); }
        finally { database.DataStorage.RowVersionLinks.Prune(database.Coordinator.PruneBound, cancellationToken); }
    }

    protected override bool HasReclamationSupport => true;
    protected override bool ReclamationReady => _engine.ProvisioningBuildComplete;
    protected override bool HasReclamation(SqlDatabase database) => database.TableReclamation.HasWork;
    protected override void RequestReclamation(SqlDatabase database) => database.TableReclamation.Request();
    protected override void Reclaim(SqlDatabase database, CancellationToken cancellationToken)
        => database.TableReclamation.RunBatch(cancellationToken);

    private sealed class DebugView
    {
        private readonly global::Assimalign.Cohesion.Database.Sql.Internal.SqlVersionPurgeWorker _target;

        public DebugView(global::Assimalign.Cohesion.Database.Sql.Internal.SqlVersionPurgeWorker value)
        {
            _target = value;
        }

        public global::Assimalign.Cohesion.Database.DatabaseWorkerLanes<global::Assimalign.Cohesion.Database.DatabasePurgeResult> Lanes => _target.Lanes;
        public bool CompletionOnly => _target.CompletionOnly;
        public string Name => _target.Name;
        public global::Assimalign.Cohesion.Database.DatabaseEngineWorkerKind Kind => _target.Kind;
        public global::System.TimeSpan Interval => _target.Interval;
    }
}

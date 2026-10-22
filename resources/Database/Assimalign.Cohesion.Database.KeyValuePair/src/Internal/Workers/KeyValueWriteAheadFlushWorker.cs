using System;
using System.Collections.Generic;
using System.Threading;

using Assimalign.Cohesion.Database.Storage;
using Assimalign.Cohesion.Database.KeyValuePair.Storage;

namespace Assimalign.Cohesion.Database.KeyValuePair.Internal;

/// <summary>
/// The engine-owned write-ahead-log group-commit flusher: woken when a grouped
/// commit registers on any open storage's durability gate, it performs one durable
/// flush per storage that covers every pending commit, so concurrent commits share a
/// single fsync.
/// </summary>
/// <remarks>
/// Signal-driven: <see cref="WaitForTrigger"/> waits on the engine's commit-pending
/// signal (every open storage's <c>OnCommitPending</c> sets it) bounded by the
/// group-commit window. The signal is reset <em>before</em> the flush pass, so a
/// commit arriving mid-pass re-sets it and is served by the next pass. In the
/// synchronous durability mode there is never anything pending and the worker idles.
/// <para>
/// A drain of the journal's append buffer (#1252) or a durable flush (#1243) that fails takes
/// its database offline: the storage releases the committers waiting on it, each gets the
/// refusal from its own flush, and the worker keeps
/// flushing the engine's other databases. Any other failure of one file set is reported for its
/// database and the pass goes on to the next (#1268); the database is flushed again after
/// <see cref="DatabaseEngineWorker.FailureBackoff"/>, and its committers self-help within their
/// window meanwhile, as they do whenever the worker is late. An offline database is skipped, and
/// so is a database its holder closed, whose close flushes both file sets.
/// </para>
/// </remarks>
internal sealed class KeyValueWriteAheadFlushWorker : DatabaseWriteWorker<KeyValueDatabase>
{
    private readonly KeyValueDatabaseEngine _engine;
    private readonly ManualResetEventSlim _commitPending;
    private KeyValueStorage[] _storages = [];
    private readonly HashSet<KeyValueStorage> _published = [];

    internal KeyValueWriteAheadFlushWorker(KeyValueDatabaseEngine engine, ManualResetEventSlim commitPending)
        : base(engine.Name + "/wal-flush", DatabaseEngineWorkerKind.WriteAheadFlush, engine.EngineOptions.Durability == StorageCommitDurability.Grouped
                ? engine.EngineOptions.GroupCommitWindow : TimeSpan.FromSeconds(1))
    {
        _engine = engine;
        _commitPending = commitPending;
    }

    protected override ManualResetEventSlim LaneSignal => _commitPending;
    protected override bool RepeatAfterCollection => true;

    protected override KeyValueDatabase[] GetDatabases()
    {
        // Publication order is storage -> instance. Never take the engine lock on a lane.
        _storages = _engine.GetStorageSnapshot();
        return _engine.GetInstanceSnapshot();
    }
    protected override string GetName(KeyValueDatabase database) => database.Name;
    protected override bool IsClosed(KeyValueDatabase database) => database.IsClosed;
    protected override bool IsOffline(KeyValueDatabase database) => database.IsOffline;
    protected override bool IsOpen(KeyValueDatabase database) => _engine.IsOpen(database);

    protected override DatabaseWriteResult RunDatabase(KeyValueDatabase database, CancellationToken cancellationToken)
    {
        DatabaseWriteResult failures = default;
        TryWrite(() => database.DataStorage.FlushPendingCommits() ? 1 : 0, ref failures);
        TryWrite(() => database.CatalogStorage.FlushPendingCommits() ? 1 : 0, ref failures);
        return failures;
    }

    protected override void PreparePass(KeyValueDatabase[] databases)
    {
        _published.Clear();
        foreach (var database in databases)
        {
            _published.Add(database.DataStorage);
            _published.Add(database.CatalogStorage);
        }
    }

    protected override void FinishPass(CancellationToken cancellationToken, DatabaseWorkerPass? pass)
    {
        foreach (var storage in _storages)
        {
            if (cancellationToken.IsCancellationRequested) { break; }
            if (_published.Contains(storage) || storage.IsOffline) { continue; }
            var visit = pass?.Add(storage.Name + " (provisional)");
            var state = Lanes.Collect(storage, out var collected);
            if (state == DatabaseWorkerLanes<DatabaseWriteResult>.LaneState.Running)
            {
                if (visit is not null) { visit.Reason = DatabaseWorkerPass.VisitReason.Running; }
                continue;
            }
            if (state == DatabaseWorkerLanes<DatabaseWriteResult>.LaneState.Ended)
            {
                if (visit is not null) { visit.Collected = true; visit.WorkUnits = collected.Result.Units; visit.Reason = collected.Failure is null ? DatabaseWorkerPass.VisitReason.Completed : DatabaseWorkerPass.VisitReason.Failed; }
                if (collected.Failure is not null || storage.IsOffline) { continue; }
                // Construction may have registered another commit after the prior flush.
                // The signal was reset before this pass; flush that registration on this visit.
            }
            if (visit is not null) { visit.Attempted = true; }
            bool ended = Lanes.Run(storage, () =>
            {
                return new DatabaseWriteResult { Units = storage.FlushPendingCommits() ? 1 : 0 };
            }, LaneSignal, LaneTimeout, cancellationToken, out var outcome, visit);
            // Construction receives its own failure. No published database exists to back off.
            if (visit is not null && visit.Reason != DatabaseWorkerPass.VisitReason.Capacity)
            {
                visit.Reason = !ended ? DatabaseWorkerPass.VisitReason.Running
                    : outcome.Failure is null ? DatabaseWorkerPass.VisitReason.Completed : DatabaseWorkerPass.VisitReason.Failed;
                if (ended) { visit.WorkUnits += outcome.Result.Units; }
            }
        }
    }
}

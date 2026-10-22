using System;
using System.Collections.Generic;
using System.Threading;

using Assimalign.Cohesion.Database.Storage;

namespace Assimalign.Cohesion.Database.Graph.Internal;

/// <summary>
/// The engine-owned write-ahead-log group-commit flusher: woken when a grouped
/// commit registers on any open storage's durability gate, it performs one durable
/// flush per open database that covers every pending commit, so concurrent commits
/// share a single fsync.
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
/// flushing the engine's other databases. Any other failure of one database is reported for it
/// and the pass goes on to the next (#1268); the database is flushed again after
/// <see cref="DatabaseEngineWorker.FailureBackoff"/>, and its committers self-help within their
/// window meanwhile, as they do whenever the worker is late.
/// </para>
/// <para>
/// The pass visits the engine's open databases and reports under the database's name in the
/// engine, never its storage's, which is read from the file header and is the original's for a
/// database opened from a copied file set (<see cref="GraphPageWriteBackWorker"/>). A database the
/// engine is still opening is not served: a commit its open makes flushes itself after its window,
/// as in the other models.
/// </para>
/// </remarks>
internal sealed class GraphWriteAheadFlushWorker : DatabaseWriteWorker<GraphDatabase>
{
    private readonly GraphDatabaseEngine _engine;
    private readonly ManualResetEventSlim _commitPending;

    internal GraphWriteAheadFlushWorker(GraphDatabaseEngine engine, ManualResetEventSlim commitPending)
        : base(engine.Name + "/wal-flush", DatabaseEngineWorkerKind.WriteAheadFlush, engine.EngineOptions.Durability == StorageCommitDurability.Grouped
                ? engine.EngineOptions.GroupCommitWindow : TimeSpan.FromSeconds(1))
    {
        _engine = engine;
        _commitPending = commitPending;
    }

    protected override ManualResetEventSlim LaneSignal => _commitPending;
    protected override bool RepeatAfterCollection => true;

    protected override GraphDatabase[] GetDatabases()
    {
        return _engine.GetInstanceSnapshot();
    }
    protected override string GetName(GraphDatabase database) => database.Name;
    protected override bool IsClosed(GraphDatabase database) => database.IsClosed;
    protected override bool IsOffline(GraphDatabase database) => database.IsOffline;
    protected override bool IsDue(GraphDatabase database) => database.DataStorage.HasPendingCommits;
    protected override bool IsOpen(GraphDatabase database) => _engine.IsOpen(database);

    protected override DatabaseWriteResult RunDatabase(GraphDatabase database, CancellationToken cancellationToken)
    {
        DatabaseWriteResult failures = default;
        TryWrite(() => database.DataStorage.FlushPendingCommits() ? 1 : 0, ref failures);
        return failures;
    }
}

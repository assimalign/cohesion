using System;
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
internal sealed class GraphWriteAheadFlushWorker : DatabaseEngineWorker
{
    private readonly GraphDatabaseEngine _engine;
    private readonly ManualResetEventSlim _commitPending;

    internal GraphWriteAheadFlushWorker(GraphDatabaseEngine engine, ManualResetEventSlim commitPending)
        : base(engine.Name + "/wal-flush", DatabaseEngineWorkerKind.WriteAheadFlush,
            engine.EngineOptions.Durability == StorageCommitDurability.Grouped
                ? engine.EngineOptions.GroupCommitWindow
                : TimeSpan.FromSeconds(1))
    {
        _engine = engine;
        _commitPending = commitPending;
    }

    /// <inheritdoc />
    protected override void WaitForTrigger(CancellationToken cancellationToken)
    {
        try
        {
            _commitPending.Wait(Interval, cancellationToken);
        }
        catch (OperationCanceledException)
        {
            // The pump is stopping; Run observes the token and exits.
        }
    }

    /// <inheritdoc />
    protected override void RunIterationCore(CancellationToken cancellationToken)
    {
        // Reset before flushing: a commit that registers mid-pass sets the signal
        // again and is picked up by the next pass instead of being lost.
        _commitPending.Reset();

        foreach (GraphDatabase database in _engine.GetInstanceSnapshot())
        {
            if (cancellationToken.IsCancellationRequested)
            {
                break;
            }

            // An offline database flushes nothing (#1243): its waiting committers were released
            // when it went offline, and each gets the refusal from its own flush. Nor does a
            // database its holder closed: the engine keeps it registered until its close ends, then
            // forgets it, and its disposed storage has no committers left to serve.
            if (database.IsClosed || database.IsOffline || !BeginDatabase(database.Name))
            {
                continue;
            }

            try
            {
                database.DataStorage.FlushPendingCommits();
            }
            catch (ObjectDisposedException) when (!_engine.IsOpen(database))
            {
                // The snapshot can race a database drop; a disposed storage has no
                // committers left to serve.
            }
            catch (Exception exception) when (exception is not OutOfMemoryException)
            {
                // The committers waiting on this database self-help within their window; the next
                // pass flushes it again. An offline database's refusal is not the worker's failure.
                if (!database.IsOffline)
                {
                    ReportFailure(database.Name, exception);
                }
            }
        }
    }
}

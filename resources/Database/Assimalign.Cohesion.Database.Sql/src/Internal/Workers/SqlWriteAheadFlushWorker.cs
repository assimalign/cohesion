using System;
using System.Collections.Generic;
using System.Threading;

namespace Assimalign.Cohesion.Database.Sql.Internal;

using Assimalign.Cohesion.Database.Sql.Storage;
using Assimalign.Cohesion.Database.Storage;

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
/// window meanwhile, as they do whenever the worker is late.
/// </para>
/// </remarks>
internal sealed class SqlWriteAheadFlushWorker : DatabaseEngineWorker
{
    private readonly SqlDatabaseEngine _engine;
    private readonly ManualResetEventSlim _commitPending;

    internal SqlWriteAheadFlushWorker(SqlDatabaseEngine engine, ManualResetEventSlim commitPending)
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

        // Take the storage snapshot before the instance snapshot: publication writes them in
        // that order. Never take the engine lock while flushing: create/open holds it while
        // waiting for the storage durability gate, which this worker must be able to release.
        var storages = _engine.GetStorageSnapshot();
        var databases = _engine.GetInstanceSnapshot();
        var published = new HashSet<SqlStorage>();
        foreach (var database in databases)
        {
            published.Add(database.DataStorage);
            published.Add(database.CatalogStorage);
        }

        foreach (SqlDatabase database in databases)
        {
            if (cancellationToken.IsCancellationRequested)
            {
                break;
            }

            // An offline database flushes nothing (#1243): its waiting committers were released
            // when it went offline, and each gets the refusal from its own flush. Nor does a
            // database its holder closed: the engine keeps it registered until its close ends, then forgets it,
            // and its disposed storages have no committers left to serve.
            if (database.IsClosed || database.IsOffline || !BeginDatabase(database.Name))
            {
                continue;
            }

            FlushPending(database, database.DataStorage);
            FlushPending(database, database.CatalogStorage);
        }

        foreach (var storage in storages)
        {
            if (cancellationToken.IsCancellationRequested)
            {
                break;
            }

            if (published.Contains(storage) || storage.IsOffline)
            {
                continue;
            }

            try
            {
                storage.FlushPendingCommits();
            }
            catch (ObjectDisposedException)
            {
                // A failed create/open disposed a storage from this older snapshot.
            }
            catch (Exception exception) when (exception is not OutOfMemoryException)
            {
                // The constructing commit receives its own flush failure. There is no
                // published database to report or back off; keep serving the other file sets.
            }
        }
    }

    private void FlushPending(SqlDatabase database, SqlStorage storage)
    {
        try
        {
            storage.FlushPendingCommits();
        }
        catch (ObjectDisposedException) when (!_engine.IsOpen(database))
        {
            // The snapshot can race a database drop; a disposed storage has no
            // committers left to serve.
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            // The committers waiting on this file set self-help within their window; the next
            // pass flushes it again. An offline database's refusal is not the worker's failure.
            if (!database.IsOffline)
            {
                ReportFailure(database.Name, exception);
            }
        }
    }
}

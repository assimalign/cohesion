using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;

namespace Assimalign.Cohesion.Database.Sql.Internal;

using Assimalign.Cohesion.Database.Storage;

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
internal sealed class SqlVersionPurgeWorker : DatabaseEngineWorker
{
    // A WaitHandle wait takes at most int.MaxValue milliseconds.
    private static readonly TimeSpan _maximumWait = TimeSpan.FromMilliseconds(int.MaxValue);

    private readonly SqlDatabaseEngine _engine;

    // The engine's clock: full passes and their retries are scheduled on it (owner decision 46).
    private readonly TimeProvider _clock;
    private long _lastFullPass;

    // The databases whose last full pass failed, each with the timestamp, on the engine's clock, at
    // which its full pass is retried (owner decisions 42 review and 46): until then a pass that only
    // retries deferred undo does not redo that work, so it reports them unfinished and their streaks
    // last until a full pass completes them. Touched only by passes, which never overlap.
    private readonly Dictionary<SqlDatabase, long> _fullPassFailed = new(ReferenceEqualityComparer.Instance);

    // The earliest of those retries, or long.MaxValue when there is none: published for the trigger
    // wait, which can run beside a pass a test runs.
    private long _nextFullPassRetry = long.MaxValue;

    internal SqlVersionPurgeWorker(SqlDatabaseEngine engine)
        : base(engine.Name + "/version-purge", DatabaseEngineWorkerKind.VersionPurge, engine.EngineOptions.MaintenanceInterval)
    {
        _engine = engine;
        _clock = engine.EngineOptions.TimeProvider ?? TimeProvider.System;
        _lastFullPass = _clock.GetTimestamp();
    }

    /// <summary>
    /// Gets or sets a hook run before a database's full pass, or null. Internal, set by this
    /// assembly's tests to fail a full pass alone, which no storage fault does without failing the
    /// deferred-undo retries too (the hook is on the type that makes the call, <c>database-area.md</c>).
    /// </summary>
    internal Action<SqlDatabase>? BeforeFullPass { get; set; }

    /// <inheritdoc />
    protected override void WaitForTrigger(CancellationToken cancellationToken)
    {
        // Until the next full pass, a failed full pass's retry (owner decision 46), or the next
        // deferred-undo retry, whichever is soonest.
        var wait = Interval - _clock.GetElapsedTime(Volatile.Read(ref _lastFullPass));
        long fullPassRetry = Volatile.Read(ref _nextFullPassRetry);
        if (fullPassRetry != long.MaxValue)
        {
            var untilRetry = _clock.GetElapsedTime(_clock.GetTimestamp(), fullPassRetry);
            if (untilRetry < wait)
            {
                wait = untilRetry;
            }
        }

        foreach (SqlDatabase database in _engine.GetInstanceSnapshot())
        {
            if (!database.IsClosed && !database.IsOffline && database.Coordinator.NextDeferredUndoRetry is { } retry && retry < wait)
            {
                wait = retry;
            }
        }

        if (wait <= TimeSpan.Zero)
        {
            return;
        }

        try
        {
            // A new deferral sets the signal, so its first retry is not left to this wait.
            _engine.UndoDeferredSignal.Wait(wait > _maximumWait ? _maximumWait : wait, cancellationToken);
        }
        catch (OperationCanceledException)
        {
            // The pump is stopping; Run observes the token and exits.
        }
        catch (ObjectDisposedException)
        {
            // The engine is closing.
        }
    }

    /// <inheritdoc />
    protected override void RunIterationCore(CancellationToken cancellationToken)
    {
        _engine.UndoDeferredSignal.Reset();
        long now = _clock.GetTimestamp();
        bool fullPass = _clock.GetElapsedTime(Volatile.Read(ref _lastFullPass), now) >= Interval;
        if (fullPass)
        {
            Volatile.Write(ref _lastFullPass, now);
        }

        try
        {
            // A database closed or no longer the engine's since its full pass failed is gone (a reopened
            // one is a new instance), so its retry, due or not, no longer wakes the worker.
            if (_fullPassFailed.Count > 0)
            {
                foreach (SqlDatabase gone in _fullPassFailed.Keys.Where(database => !_engine.IsOpen(database)).ToArray())
                {
                    _fullPassFailed.Remove(gone);
                }
            }

            foreach (SqlDatabase database in _engine.GetInstanceSnapshot())
            {
                if (cancellationToken.IsCancellationRequested)
                {
                    break;
                }

                // An offline database is not begun: the engine reports it (#1243), and a failure the
                // worker recorded for it ends. Nor is a database its holder closed: the engine keeps it
                // registered until its close ends, then forgets it, and its disposed coordinator has nothing left
                // to purge.
                if (database.IsClosed || database.IsOffline)
                {
                    _fullPassFailed.Remove(database);
                    continue;
                }

                if (!BeginDatabase(database.Name))
                {
                    continue;
                }

                // The engine's full pass, or the retry of a full pass that failed on this database
                // once the backoff has passed (owner decision 46).
                bool full = fullPass || (_fullPassFailed.TryGetValue(database, out long retryAt) && now >= retryAt);
                try
                {
                    if (full)
                    {
                        BeforeFullPass?.Invoke(database);
                        database.Coordinator.RunVersionPurgePass(cancellationToken);
                        _fullPassFailed.Remove(database);
                    }
                    else
                    {
                        database.Coordinator.RetryDeferredUndo(cancellationToken);
                    }

                    // A deferred undo still waiting, or a failed full pass this retry did not redo,
                    // keeps the database's failure recorded until a pass leaves nothing over.
                    if (database.Coordinator.NextDeferredUndoRetry is not null || _fullPassFailed.ContainsKey(database))
                    {
                        ReportUnfinished(database.Name);
                    }
                }
                catch (StorageTransactionException)
                {
                    // A storage bracket is active on this database; retry next pass. A failed full
                    // pass found busy is retried a backoff later, so the retry does not spin.
                    if (full && _fullPassFailed.ContainsKey(database))
                    {
                        ScheduleRetry(database);
                    }

                    ReportUnfinished(database.Name);
                }
                catch (ObjectDisposedException) when (!_engine.IsOpen(database))
                {
                    // The snapshot can race a database drop; nothing left to purge.
                    _fullPassFailed.Remove(database);
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    break;
                }
                catch (Exception exception) when (exception is not OutOfMemoryException)
                {
                    // Recorded, not fatal: the writer whose undo failed again keeps its place in the
                    // retry schedule its coordinator paces (#1226), so the worker holds nothing back,
                    // and every other database keeps its maintenance. A database that went offline
                    // during the pass (#1243) is skipped by the next pass instead. A failed full
                    // pass is retried a backoff later (owner decision 46).
                    if (!database.IsOffline)
                    {
                        ReportFailure(database.Name, exception, TimeSpan.Zero);
                        if (full)
                        {
                            ScheduleRetry(database);
                        }
                    }
                }
            }
        }
        finally
        {
            PublishNextRetry();
        }
    }

    // Retries the database's full pass a backoff from now, on the engine's clock.
    private void ScheduleRetry(SqlDatabase database)
        => _fullPassFailed[database] = _clock.GetTimestamp() + (long)(FailureBackoff.TotalSeconds * _clock.TimestampFrequency);

    // Publishes the earliest full-pass retry for the trigger wait.
    private void PublishNextRetry()
    {
        long next = long.MaxValue;
        foreach (long retryAt in _fullPassFailed.Values)
        {
            if (retryAt < next)
            {
                next = retryAt;
            }
        }

        Volatile.Write(ref _nextFullPassRetry, next);
    }
}

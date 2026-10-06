using System;
using System.Diagnostics;
using System.Threading;

namespace Assimalign.Cohesion.Database.Graph.Internal;

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
/// (<see cref="GraphDatabaseEngineOptions.MaintenanceInterval"/>) — embedded and
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
/// whose storage was busy, keeps a failure recorded for it until a pass leaves nothing over; a
/// failure of another database does not keep it, so a transient fault does not leave the engine
/// Faulted for good. An offline database (#1243) is skipped.
/// </para>
/// </remarks>
internal sealed class GraphVersionPurgeWorker : DatabaseEngineWorker
{
    // A WaitHandle wait takes at most int.MaxValue milliseconds.
    private static readonly TimeSpan _maximumWait = TimeSpan.FromMilliseconds(int.MaxValue);

    private readonly GraphDatabaseEngine _engine;
    private long _lastFullPass = Stopwatch.GetTimestamp();

    internal GraphVersionPurgeWorker(GraphDatabaseEngine engine)
        : base(engine.Name + "/version-purge", DatabaseEngineWorkerKind.VersionPurge, engine.EngineOptions.MaintenanceInterval)
    {
        _engine = engine;
    }

    /// <inheritdoc />
    protected override void WaitForTrigger(CancellationToken cancellationToken)
    {
        // Until the next full pass, or the next deferred-undo retry when one is sooner.
        var wait = Interval - Stopwatch.GetElapsedTime(Volatile.Read(ref _lastFullPass));
        foreach (GraphDatabase database in _engine.GetInstanceSnapshot())
        {
            if (!database.IsOffline && database.Coordinator.NextDeferredUndoRetry is { } retry && retry < wait)
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
        bool fullPass = Stopwatch.GetElapsedTime(Volatile.Read(ref _lastFullPass)) >= Interval;
        if (fullPass)
        {
            Volatile.Write(ref _lastFullPass, Stopwatch.GetTimestamp());
        }

        foreach (GraphDatabase database in _engine.GetInstanceSnapshot())
        {
            if (cancellationToken.IsCancellationRequested)
            {
                break;
            }

            // An offline database is not begun: the engine reports it (#1243), and a failure the
            // worker recorded for it ends.
            if (database.IsOffline || !BeginDatabase(database.Name))
            {
                continue;
            }

            try
            {
                if (fullPass)
                {
                    database.Coordinator.RunVersionPurgePass(cancellationToken);
                }
                else
                {
                    database.Coordinator.RetryDeferredUndo(cancellationToken);
                }

                if (database.Coordinator.NextDeferredUndoRetry is not null)
                {
                    ReportUnfinished(database.Name);
                }
            }
            catch (StorageTransactionException)
            {
                // A storage bracket is active on this database; retry next pass.
                ReportUnfinished(database.Name);
            }
            catch (ObjectDisposedException) when (!_engine.IsOpen(database))
            {
                // The snapshot can race a database drop; nothing left to purge.
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
                // during the pass (#1243) is skipped by the next pass instead.
                if (!database.IsOffline)
                {
                    ReportFailure(database.Name, exception, TimeSpan.Zero);
                }
            }
        }
    }
}

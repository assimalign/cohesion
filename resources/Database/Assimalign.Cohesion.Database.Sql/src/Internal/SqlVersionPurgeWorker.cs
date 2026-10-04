using System;
using System.Diagnostics;
using System.Threading;

namespace Assimalign.Cohesion.Database.Sql.Internal;

using Assimalign.Cohesion.Database.Storage;

/// <summary>
/// The engine-owned MVCC version-purge worker: per pass, per open database, it
/// retries the logical undo of any aborted writer whose rollback-time purge
/// failed (<c>IVersionStore.PurgeWriterAsync</c>) and physically reclaims
/// versions no snapshot can reach — committed tombstones below the safe prune
/// bound (<c>IVersionStore.PruneAsync</c>) — so version-space amplification is
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
/// A failure is recorded — the engine's observational state flips to Faulted — and the worker
/// keeps running: an undo that fails again is retried at its next delay, and unpurged versions
/// cost space, never consistency. An offline database (#1243) is skipped.
/// </para>
/// </remarks>
internal sealed class SqlVersionPurgeWorker : DatabaseEngineWorker
{
    private readonly SqlDatabaseEngine _engine;
    private long _lastFullPass = Stopwatch.GetTimestamp();

    internal SqlVersionPurgeWorker(SqlDatabaseEngine engine)
    {
        _engine = engine;
    }

    /// <inheritdoc />
    public override string Name => _engine.Name + "/version-purge";

    /// <inheritdoc />
    public override DatabaseEngineWorkerKind Kind => DatabaseEngineWorkerKind.VersionPurge;

    /// <inheritdoc />
    public override TimeSpan Interval => _engine.EngineOptions.MaintenanceInterval;

    /// <inheritdoc />
    protected override void WaitForTrigger(CancellationToken cancellationToken)
    {
        // Until the next full pass, or the next deferred-undo retry when one is sooner.
        var wait = Interval - Stopwatch.GetElapsedTime(Volatile.Read(ref _lastFullPass));
        foreach (SqlDatabaseInstance database in _engine.GetInstanceSnapshot())
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
            _engine.UndoDeferredSignal.Wait(wait > MaximumWait ? MaximumWait : wait, cancellationToken);
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
    public override void RunIteration(CancellationToken cancellationToken)
    {
        _engine.UndoDeferredSignal.Reset();
        bool fullPass = Stopwatch.GetElapsedTime(Volatile.Read(ref _lastFullPass)) >= Interval;
        if (fullPass)
        {
            Volatile.Write(ref _lastFullPass, Stopwatch.GetTimestamp());
        }

        foreach (SqlDatabaseInstance database in _engine.GetInstanceSnapshot())
        {
            if (cancellationToken.IsCancellationRequested)
            {
                break;
            }

            if (database.IsOffline)
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
            }
            catch (StorageTransactionException)
            {
                // A storage bracket is active on this database; retry next pass.
            }
            catch (ObjectDisposedException)
            {
                // The snapshot can race a database drop; nothing left to purge.
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception exception) when (exception is not OutOfMemoryException && !database.IsOffline)
            {
                // Recorded, not fatal: the writer whose undo failed again keeps its place in the
                // retry schedule, and every other database keeps its maintenance.
                _engine.ReportWorkerFault(exception);
            }
            catch (Exception exception) when (exception is not OutOfMemoryException)
            {
                // The database went offline during the pass (#1243); the next pass skips it.
            }
        }
    }

    // A WaitHandle wait takes at most int.MaxValue milliseconds.
    private static readonly TimeSpan MaximumWait = TimeSpan.FromMilliseconds(int.MaxValue);
}

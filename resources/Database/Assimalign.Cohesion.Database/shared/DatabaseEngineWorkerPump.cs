using System;
using System.Collections.Generic;
using System.Threading;

// Deviates from namespace-matches-assembly: this model-local helper is compiled
// from the Database root's shared source into each model assembly.
namespace Assimalign.Cohesion.Database;

/// <summary>
/// The worker pump every engine model runs its workers on, and the engine state it reports from
/// them (#1268): one copy of the behavior the five engines share (database-area.md rule 8). Since
/// phase 3 of the concrete-types plan (#1259) the root <see cref="DatabaseEngine"/> base carries
/// the same pump and fold; a model stops compiling this copy in its phase-4 PR, when its engine
/// derives from the base, and the last of those PRs deletes it.
/// </summary>
internal static class DatabaseEngineWorkerPump
{
    /// <summary>
    /// The worker pump frame: runs <paramref name="worker"/> until <paramref name="cancellationToken"/>
    /// is signaled, and never lets it end before that. A <see cref="DatabaseEngineWorker"/> catches
    /// every failure per pass and records it, so its <see cref="IDatabaseEngineWorker.Run"/> returns
    /// only on cancellation. A worker that implements the interface alone may let an exception
    /// escape, or return early: the pump records that in <paramref name="runFault"/> (the engine
    /// reports <see cref="EngineState.Faulted"/> until disposal, since nothing tells it when such a
    /// worker is healthy again), sleeps <see cref="DatabaseEngineWorker.FailureBackoff"/>, and runs
    /// it again. Only an <see cref="OutOfMemoryException"/> escapes the thread, which ends the process.
    /// </summary>
    /// <param name="worker">The worker to run.</param>
    /// <param name="cancellationToken">Signaled when the engine stops its workers.</param>
    /// <param name="runFault">The engine's record of the last exception that escaped a worker's loop.</param>
    public static void Pump(IDatabaseEngineWorker worker, CancellationToken cancellationToken, ref Exception? runFault)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                worker.Run(cancellationToken);
                if (cancellationToken.IsCancellationRequested)
                {
                    return;
                }

                Volatile.Write(ref runFault, new InvalidOperationException(
                    $"Worker '{worker.Name}' returned from Run before its engine stopped it; the engine runs it again."));
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception exception) when (exception is not OutOfMemoryException)
            {
                Volatile.Write(ref runFault, exception);
            }

            cancellationToken.WaitHandle.WaitOne(DatabaseEngineWorker.FailureBackoff);
        }
    }

    /// <summary>
    /// Folds an engine's state from its lifecycle and its workers:
    /// <see cref="EngineState.Disposed"/> after disposal, <see cref="EngineState.Faulted"/> while a
    /// worker's loop has failed (<paramref name="runFault"/>) or a guided worker holds a failure
    /// (<see cref="DatabaseEngineWorker.Fault"/>), and <see cref="EngineState.Running"/> otherwise.
    /// An offline database is not a worker failure; the engine lists it in
    /// <see cref="IDatabaseEngine.OfflineDatabases"/>.
    /// </summary>
    /// <param name="disposed">Whether the engine was disposed.</param>
    /// <param name="runFault">The last exception that escaped a worker's loop, or null.</param>
    /// <param name="workers">The engine's published worker inventory.</param>
    /// <returns>The engine's state.</returns>
    public static EngineState Fold(bool disposed, Exception? runFault, IReadOnlyList<IDatabaseEngineWorker> workers)
    {
        if (disposed)
        {
            return EngineState.Disposed;
        }

        if (runFault is not null)
        {
            return EngineState.Faulted;
        }

        for (int index = 0; index < workers.Count; index++)
        {
            if (workers[index] is DatabaseEngineWorker { Fault: not null })
            {
                return EngineState.Faulted;
            }
        }

        return EngineState.Running;
    }
}

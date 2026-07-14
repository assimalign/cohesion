using System;
using System.Threading;

namespace Assimalign.Cohesion.Database;

/// <summary>
/// The guided base class for engine-owned background workers: implements the
/// blocking pump loop so an implementer only supplies the trigger wait and the
/// per-pass work.
/// </summary>
/// <remarks>
/// The public contract (<see cref="IDatabaseEngineWorker"/>) is observational; the
/// pump members on this base — <see cref="Run"/>, <see cref="RunIteration"/>,
/// <see cref="WaitForTrigger"/> — exist for the <em>owning engine</em>, which spawns
/// one dedicated thread (or timer loop) per worker at engine creation and cancels it
/// on dispose. Nothing outside the engine may pump a worker; a worker never runs
/// twice concurrently because exactly one engine-internal scheduler drives it.
/// The default <see cref="Run"/> loop alternates <see cref="WaitForTrigger"/> and
/// <see cref="RunIteration"/> until cancellation. Timer-paced workers inherit the
/// default trigger (wait out <see cref="Interval"/>); signal-driven workers (a
/// group-commit flusher woken by pending commits) override
/// <see cref="WaitForTrigger"/> with their own wake condition.
/// </remarks>
public abstract class DatabaseEngineWorker : IDatabaseEngineWorker
{
    /// <summary>
    /// Initializes a new worker.
    /// </summary>
    protected DatabaseEngineWorker() { }

    /// <inheritdoc />
    public abstract string Name { get; }

    /// <inheritdoc />
    public abstract DatabaseEngineWorkerKind Kind { get; }

    /// <inheritdoc />
    public abstract TimeSpan Interval { get; }

    /// <summary>
    /// Runs the worker's pump until <paramref name="cancellationToken"/> is signaled:
    /// waits for the worker's trigger (its <see cref="Interval"/> or an internal work
    /// signal), performs one pass, and repeats. Blocking — the owning engine calls
    /// this on the dedicated thread it spawns for the worker.
    /// </summary>
    /// <param name="cancellationToken">Signaled to stop the pump.</param>
    public virtual void Run(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            WaitForTrigger(cancellationToken);

            if (cancellationToken.IsCancellationRequested)
            {
                break;
            }

            RunIteration(cancellationToken);
        }
    }

    /// <summary>
    /// Performs one bounded pump pass without waiting. Called by <see cref="Run"/>;
    /// the owning engine may also tick it directly from its own timer loop.
    /// </summary>
    /// <param name="cancellationToken">Cancels the pass.</param>
    /// <exception cref="OperationCanceledException">Thrown when <paramref name="cancellationToken"/> is signaled mid-pass.</exception>
    public abstract void RunIteration(CancellationToken cancellationToken);

    /// <summary>
    /// Blocks until the worker's next pump pass should run. The default waits out
    /// <see cref="Interval"/> (returning early on cancellation); signal-driven
    /// workers override this with their own wake condition.
    /// </summary>
    /// <param name="cancellationToken">Signaled to stop the pump; the wait must return promptly.</param>
    protected virtual void WaitForTrigger(CancellationToken cancellationToken)
        => cancellationToken.WaitHandle.WaitOne(Interval);
}

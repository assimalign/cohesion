using System;
using System.Threading;

namespace Assimalign.Cohesion.Database;

/// <summary>
/// The guided base class for engine-owned background workers: implements the claim
/// handshake and the blocking pump loop so an implementer only supplies the trigger
/// wait and the per-pass work.
/// </summary>
/// <remarks>
/// The default <see cref="Run"/> loop alternates <see cref="WaitForTrigger"/> and
/// <see cref="RunIteration"/> until cancellation. Timer-paced workers inherit the
/// default trigger (wait out <see cref="Interval"/>); signal-driven workers (a
/// group-commit flusher woken by pending commits) override
/// <see cref="WaitForTrigger"/> with their own wake condition.
/// </remarks>
public abstract class DatabaseEngineWorker : IDatabaseEngineWorker
{
    private int _claimed;

    /// <summary>
    /// Initializes a new worker in the unclaimed state.
    /// </summary>
    protected DatabaseEngineWorker() { }

    /// <inheritdoc />
    public abstract string Name { get; }

    /// <inheritdoc />
    public abstract DatabaseEngineWorkerKind Kind { get; }

    /// <inheritdoc />
    public abstract TimeSpan Interval { get; }

    /// <inheritdoc />
    public bool TryClaim()
        => Interlocked.CompareExchange(ref _claimed, 1, 0) == 0;

    /// <inheritdoc />
    public void Release()
        => Volatile.Write(ref _claimed, 0);

    /// <inheritdoc />
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

    /// <inheritdoc />
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

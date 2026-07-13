using System;
using System.Threading;

namespace Assimalign.Cohesion.Database;

/// <summary>
/// An engine-owned background work loop (checkpointing, write-ahead-log flushing,
/// page write-back, version purge, index maintenance) exposed so a host can map it
/// onto its own execution model.
/// </summary>
/// <remarks>
/// <para>
/// <b>The engine owns the work; the scheduler is pluggable.</b> Workers exist from
/// engine creation and their bodies live inside the engine — an embedded consumer
/// (no host) gets identical durability behavior because
/// <see cref="IDatabaseEngine.StartAsync"/> self-schedules every worker nobody else
/// claimed. A host that wants to place a worker on its own execution menu claims it
/// with <see cref="TryClaim"/> <em>before</em> starting the engine and then drives
/// <see cref="Run"/> (a blocking pump for a dedicated thread) or
/// <see cref="RunIteration"/> (one bounded pass, for a timer loop) itself.
/// </para>
/// <para>
/// <b>Single ownership.</b> A worker never runs twice concurrently: exactly one
/// claimant may pump it. The engine claims unclaimed workers when it starts and
/// releases them when it stops; a host's claim is taken at composition time and held
/// for the composition's lifetime, so an engine restart under the same host leaves
/// ownership unchanged. Claiming after the engine started fails — the engine already
/// self-scheduled the worker.
/// </para>
/// <para>
/// Pumping a worker whose engine is not running is a safe no-op; workers observe
/// engine state and idle until work exists.
/// </para>
/// </remarks>
public interface IDatabaseEngineWorker
{
    /// <summary>
    /// Gets the diagnostic name of this worker, unique within its engine
    /// (for example <c>sql-engine/checkpoint</c>).
    /// </summary>
    string Name { get; }

    /// <summary>
    /// Gets the role of this worker, which tells a host which execution-menu member
    /// the worker belongs on.
    /// </summary>
    DatabaseEngineWorkerKind Kind { get; }

    /// <summary>
    /// Gets the cadence of the worker's pump: the interval a timer-driven scheduler
    /// ticks <see cref="RunIteration"/> at, and the bound on how long a blocking
    /// <see cref="Run"/> pump waits between passes. Configured through the owning
    /// engine's options.
    /// </summary>
    TimeSpan Interval { get; }

    /// <summary>
    /// Atomically claims scheduling ownership of this worker.
    /// </summary>
    /// <returns>
    /// <see langword="true"/> when the caller now owns the worker's pump;
    /// <see langword="false"/> when the engine or another host already owns it.
    /// </returns>
    bool TryClaim();

    /// <summary>
    /// Releases a claim taken with <see cref="TryClaim"/>. Only the current owner
    /// may release; the engine releases its self-scheduled claims when it stops.
    /// </summary>
    void Release();

    /// <summary>
    /// Runs the worker's pump until <paramref name="cancellationToken"/> is signaled:
    /// waits for the worker's trigger (its <see cref="Interval"/> or an internal work
    /// signal), performs one pass, and repeats. Blocking — intended for a dedicated
    /// thread. Only the claim owner may call this, and never concurrently with
    /// <see cref="RunIteration"/>.
    /// </summary>
    /// <param name="cancellationToken">Signaled to stop the pump.</param>
    void Run(CancellationToken cancellationToken);

    /// <summary>
    /// Performs one bounded pump pass without waiting — the seam a timer-driven
    /// scheduler ticks. Only the claim owner may call this, and never concurrently
    /// with <see cref="Run"/>.
    /// </summary>
    /// <param name="cancellationToken">Cancels the pass.</param>
    /// <exception cref="OperationCanceledException">Thrown when <paramref name="cancellationToken"/> is signaled mid-pass.</exception>
    void RunIteration(CancellationToken cancellationToken);
}

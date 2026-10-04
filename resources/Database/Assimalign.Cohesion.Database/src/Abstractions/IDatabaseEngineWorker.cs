using System;
using System.Threading;

namespace Assimalign.Cohesion.Database;

/// <summary>
/// An engine-owned background work loop (checkpointing, write-ahead-log flushing,
/// page write-back, version purge, index maintenance), exposed for observability:
/// its name, its role, and its cadence.
/// </summary>
/// <remarks>
/// <b>The engine owns the work and its scheduling.</b> Workers spawn when their
/// engine is created and quiesce when it is disposed — an embedded consumer and a
/// hosted server get identical durability behavior because nothing outside the
/// engine participates in running these loops. Diagnostics can enumerate workers
/// and read their cadence. The engine alone calls <see cref="Run"/>; the guided
/// <see cref="DatabaseEngineWorker"/> base supplies its usual trigger-and-pass loop.
/// </remarks>
public interface IDatabaseEngineWorker
{
    /// <summary>
    /// Gets the diagnostic name of this worker, unique within its engine
    /// (for example <c>sql-engine/checkpoint</c>).
    /// </summary>
    string Name { get; }

    /// <summary>
    /// Gets the role of this worker.
    /// </summary>
    DatabaseEngineWorkerKind Kind { get; }

    /// <summary>
    /// Gets the cadence of the worker's pump: the bound on how long the loop waits
    /// between passes. Configured through the owning engine's options.
    /// </summary>
    TimeSpan Interval { get; }

    /// <summary>Runs the worker pump on the owning engine's dedicated thread until cancellation.</summary>
    /// <param name="cancellationToken">Signaled when the engine quiesces its workers.</param>
    /// <remarks>
    /// <see cref="DatabaseEngineWorker"/>'s loop never lets a failure escape: it records each failed
    /// pass and backs off (#1268). A worker that implements this interface without the base and
    /// lets an exception escape, or returns before cancellation, is restarted by its engine after
    /// <see cref="DatabaseEngineWorker.FailureBackoff"/>, and the engine reports
    /// <see cref="EngineState.Faulted"/> from then until it is disposed: without the base's pass
    /// record the engine cannot tell when such a worker is healthy again.
    /// </remarks>
    void Run(CancellationToken cancellationToken);
}

using System;

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
/// engine participates in running these loops. This contract is deliberately
/// observational: diagnostics, health surfaces, and tests can enumerate an engine's
/// workers (<see cref="IDatabaseEngine.Workers"/>) and read their cadence, but the
/// pump machinery lives on the guided base class
/// (<see cref="DatabaseEngineWorker"/>) for the engine's internal use only.
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
}

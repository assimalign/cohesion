using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace Assimalign.Cohesion.Database;

/// <summary>
/// The lifecycle surface of a database engine: its state, its engine-owned
/// background workers, and the start/stop transitions a host or embedded
/// consumer drives.
/// </summary>
/// <remarks>
/// Separated from <see cref="IDatabaseEngine"/> so lifecycle concerns and
/// database operations live on distinct contracts: composition surfaces (a host
/// service, the application) depend on this interface alone and can drive any
/// engine without seeing its data operations, while data-path consumers (the
/// server, sessions) hold <see cref="IDatabaseEngine"/> for the operations.
/// Disposal deliberately stays on <see cref="IDatabaseEngine"/> — a lifecycle
/// holder starts and stops an engine it does not own; the composition that
/// created the engine disposes it.
/// </remarks>
public interface IDatabaseEngineLifecycle
{
    /// <summary>
    /// Gets the current lifecycle state of the engine.
    /// </summary>
    EngineState State { get; }

    /// <summary>
    /// Gets the engine-owned background workers (checkpointing, write-ahead-log
    /// flushing, page write-back, maintenance). Workers exist from engine creation so
    /// a host can claim them (<see cref="IDatabaseEngineWorker.TryClaim"/>) before
    /// <see cref="StartAsync"/>; the engine self-schedules every worker still
    /// unclaimed when it starts, so embedded consumers get identical behavior with no
    /// host at all. See <see cref="IDatabaseEngineWorker"/> for the ownership rules.
    /// </summary>
    IReadOnlyList<IDatabaseEngineWorker> Workers { get; }

    /// <summary>
    /// Starts the engine: composes its internal subsystems (storage strategy, background
    /// workers) and transitions it to <see cref="EngineState.Running"/>. Databases can be
    /// created, opened, and dropped only while the engine is running.
    /// </summary>
    /// <param name="cancellationToken">Cancellation token for the operation.</param>
    /// <returns>A task that completes once the engine is running.</returns>
    /// <remarks>
    /// Starting an engine that is already <see cref="EngineState.Running"/> is a no-op.
    /// A stopped engine may be started again; previously created databases must then be
    /// reopened. State transitions: <see cref="EngineState.Idle"/> or
    /// <see cref="EngineState.Stopped"/> → <see cref="EngineState.Starting"/> →
    /// <see cref="EngineState.Running"/>.
    /// </remarks>
    /// <exception cref="DatabaseException">
    /// Thrown when the engine is <see cref="EngineState.Faulted"/>, or when a lifecycle
    /// transition (<see cref="EngineState.Starting"/>/<see cref="EngineState.Stopping"/>)
    /// is already in progress.
    /// </exception>
    /// <exception cref="ObjectDisposedException">Thrown when the engine has been disposed.</exception>
    /// <exception cref="OperationCanceledException">Thrown when <paramref name="cancellationToken"/> is signaled before the start completes.</exception>
    Task StartAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Stops the engine: quiesces its background workers, durably flushes and closes all
    /// open databases, and transitions it to <see cref="EngineState.Stopped"/>. Work
    /// committed before the stop is durable when the returned task completes.
    /// </summary>
    /// <param name="cancellationToken">Cancellation token bounding the stop.</param>
    /// <returns>A task that completes once the engine has stopped.</returns>
    /// <remarks>
    /// Stopping an engine that is not <see cref="EngineState.Running"/> is a no-op.
    /// A stopped engine may be started again with <see cref="StartAsync"/>. State
    /// transitions: <see cref="EngineState.Running"/> → <see cref="EngineState.Stopping"/>
    /// → <see cref="EngineState.Stopped"/>.
    /// </remarks>
    /// <exception cref="DatabaseException">Thrown when a lifecycle transition is already in progress.</exception>
    /// <exception cref="ObjectDisposedException">Thrown when the engine has been disposed.</exception>
    /// <exception cref="OperationCanceledException">Thrown when <paramref name="cancellationToken"/> is signaled before the stop completes.</exception>
    Task StopAsync(CancellationToken cancellationToken = default);
}

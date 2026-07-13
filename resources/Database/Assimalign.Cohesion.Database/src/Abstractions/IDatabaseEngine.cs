using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace Assimalign.Cohesion.Database;

/// <summary>
/// Represents a database engine that manages the lifecycle of logical database instances.
/// </summary>
/// <remarks>
/// Each engine implementation is model-specific (SQL, Document, Graph, or Key-Value).
/// Subsystems such as storage, write-ahead logging, and indexing are composed internally
/// by the engine and are not exposed on this interface.
/// </remarks>
public interface IDatabaseEngine : IAsyncDisposable, IDisposable
{
    /// <summary>
    /// Gets the logical name of this engine instance.
    /// </summary>
    string Name { get; }

    /// <summary>
    /// Gets the current lifecycle state of the engine.
    /// </summary>
    EngineState State { get; }

    /// <summary>
    /// Gets the data model this engine implements (SQL, Documents, Graph, Blob, or KeyValue).
    /// </summary>
    EngineModel Model { get; }

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

    /// <summary>
    /// Creates a new logical database with the specified name.
    /// </summary>
    /// <param name="name">The name of the database to create.</param>
    /// <param name="cancellationToken">Cancellation token for the operation.</param>
    /// <returns>The newly created database instance.</returns>
    /// <exception cref="DatabaseException">Thrown when a database with the same name already exists.</exception>
    ValueTask<IDatabase> CreateDatabaseAsync(string name, CancellationToken cancellationToken = default);

    /// <summary>
    /// Opens an existing logical database by name.
    /// </summary>
    /// <param name="name">The name of the database to open.</param>
    /// <param name="cancellationToken">Cancellation token for the operation.</param>
    /// <returns>The opened database instance.</returns>
    /// <exception cref="DatabaseException">Thrown when the database does not exist.</exception>
    ValueTask<IDatabase> OpenDatabaseAsync(string name, CancellationToken cancellationToken = default);

    /// <summary>
    /// Drops an existing logical database and its associated storage.
    /// </summary>
    /// <param name="name">The name of the database to drop.</param>
    /// <param name="cancellationToken">Cancellation token for the operation.</param>
    /// <exception cref="DatabaseException">Thrown when the database does not exist.</exception>
    ValueTask DropDatabaseAsync(string name, CancellationToken cancellationToken = default);

    /// <summary>
    /// Enumerates all logical databases managed by this engine.
    /// </summary>
    /// <param name="cancellationToken">Cancellation token for the operation.</param>
    /// <returns>An async sequence of database instances.</returns>
    IAsyncEnumerable<IDatabase> GetDatabasesAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Attempts to retrieve a database by name without throwing.
    /// </summary>
    /// <param name="name">The name of the database.</param>
    /// <param name="database">When this method returns true, the database instance.</param>
    /// <returns>True if the database exists; otherwise false.</returns>
    bool TryGetDatabase(string name, out IDatabase database);
}

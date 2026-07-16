using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace Assimalign.Cohesion.Database;

/// <summary>
/// Represents a database engine that manages logical database instances.
/// </summary>
/// <remarks>
/// Each engine implementation is model-specific (SQL, Document, Graph, Blob, or
/// Key-Value). Subsystems such as storage, write-ahead logging, indexing, and the
/// engine's background workers are composed internally by the engine and are not
/// exposed on this interface.
/// <para>
/// <b>An engine is a data machine, not a service:</b> it is fully operational from
/// creation — its background workers (<see cref="Workers"/>) spawn with it — and it
/// has no start/stop ceremony. Disposal is the engine's one lifecycle transition:
/// it quiesces the background workers, durably flushes every open database, and
/// closes them — work committed before disposal is durable when
/// <see cref="IAsyncDisposable.DisposeAsync"/> completes. Disposal is idempotent.
/// "Running" belongs to the things built <em>on</em> engines (an
/// <see cref="IDatabaseServer"/> fronting one engine on the network, an
/// <see cref="IDatabaseApplication"/> composing servers), never to the engine
/// itself.
/// </para>
/// </remarks>
public interface IDatabaseEngine : IAsyncDisposable, IDisposable
{
    /// <summary>
    /// Gets the logical name of this engine instance.
    /// </summary>
    string Name { get; }

    /// <summary>
    /// Gets the data model this engine implements (SQL, Documents, Graph, Blob, or KeyValue).
    /// </summary>
    EngineModel Model { get; }

    /// <summary>
    /// Gets the observational state of the engine: <see cref="EngineState.Running"/>
    /// from creation, <see cref="EngineState.Faulted"/> when a background worker
    /// fault was recorded (the engine keeps serving — durability self-help holds,
    /// but the owner should learn the engine runs degraded), and
    /// <see cref="EngineState.Disposed"/> after disposal.
    /// </summary>
    EngineState State { get; }

    /// <summary>
    /// Gets the engine-owned background workers (checkpointing, write-ahead-log
    /// flushing, page write-back, maintenance), for observability: name, role, and
    /// cadence. Workers spawn when the engine is created and quiesce when it is
    /// disposed; scheduling is engine-internal and not composable from outside.
    /// </summary>
    IReadOnlyList<IDatabaseEngineWorker> Workers { get; }

    /// <summary>
    /// Creates a new logical database with the specified name.
    /// </summary>
    /// <param name="name">The name of the database to create.</param>
    /// <param name="cancellationToken">Cancellation token for the operation.</param>
    /// <returns>The newly created database instance.</returns>
    /// <exception cref="DatabaseException">Thrown when a database with the same name already exists.</exception>
    /// <exception cref="ObjectDisposedException">Thrown when the engine has been disposed.</exception>
    ValueTask<IDatabase> CreateDatabaseAsync(string name, CancellationToken cancellationToken = default);

    /// <summary>
    /// Opens an existing logical database by name.
    /// </summary>
    /// <param name="name">The name of the database to open.</param>
    /// <param name="cancellationToken">Cancellation token for the operation.</param>
    /// <returns>The opened database instance.</returns>
    /// <exception cref="DatabaseException">Thrown when the database does not exist.</exception>
    /// <exception cref="ObjectDisposedException">Thrown when the engine has been disposed.</exception>
    ValueTask<IDatabase> OpenDatabaseAsync(string name, CancellationToken cancellationToken = default);

    /// <summary>
    /// Drops an existing logical database and its associated storage.
    /// </summary>
    /// <param name="name">The name of the database to drop.</param>
    /// <param name="cancellationToken">Cancellation token for the operation.</param>
    /// <exception cref="DatabaseException">Thrown when the database does not exist.</exception>
    /// <exception cref="ObjectDisposedException">Thrown when the engine has been disposed.</exception>
    ValueTask DropDatabaseAsync(string name, CancellationToken cancellationToken = default);

    /// <summary>
    /// Enumerates all logical databases managed by this engine.
    /// </summary>
    /// <param name="cancellationToken">Cancellation token for the operation.</param>
    /// <returns>An async sequence of database instances.</returns>
    /// <exception cref="ObjectDisposedException">Thrown when the engine has been disposed.</exception>
    IAsyncEnumerable<IDatabase> GetDatabasesAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Attempts to retrieve a database by name without throwing.
    /// </summary>
    /// <param name="name">The name of the database.</param>
    /// <param name="database">When this method returns true, the database instance.</param>
    /// <returns>True if the database exists; otherwise false.</returns>
    /// <exception cref="ObjectDisposedException">Thrown when the engine has been disposed.</exception>
    bool TryGetDatabase(string name, out IDatabase database);
}

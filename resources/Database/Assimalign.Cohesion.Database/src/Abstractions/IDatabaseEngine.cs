using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace Assimalign.Cohesion.Database;

/// <summary>
/// Represents a database engine that manages logical database instances.
/// </summary>
/// <remarks>
/// Each engine implementation is model-specific (SQL, Document, Graph, or Key-Value).
/// Subsystems such as storage, write-ahead logging, and indexing are composed internally
/// by the engine and are not exposed on this interface. Lifecycle members (state,
/// workers, start/stop) are declared on the inherited
/// <see cref="IDatabaseEngineLifecycle"/> so composition surfaces can drive an engine's
/// lifecycle without seeing its database operations; this interface declares the
/// engine's identity and its data-path operations.
/// </remarks>
public interface IDatabaseEngine : IDatabaseEngineLifecycle, IAsyncDisposable, IDisposable
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

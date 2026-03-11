using System;
using System.Threading;
using System.Threading.Tasks;

namespace Assimalign.Cohesion.Database;

/// <summary>
/// Represents a logical database instance managed by an engine.
/// </summary>
/// <remarks>
/// Model-specific databases (SQL, Document, Graph, Key-Value) extend this interface
/// with additional operations relevant to their data model. The storage model is
/// determined by the engine type — <c>StorageModel</c> remains on <c>IStorage</c>
/// at the physical layer, not here.
/// </remarks>
public interface IDatabase : IAsyncDisposable, IDisposable
{
    /// <summary>
    /// Gets the name of this database.
    /// </summary>
    DatabaseName Name { get; }

    /// <summary>
    /// Gets the engine that owns this database instance.
    /// </summary>
    IDatabaseEngine Engine { get; }

    /// <summary>
    /// Creates a new lightweight session scoped to this database.
    /// </summary>
    /// <param name="cancellationToken">Cancellation token for the operation.</param>
    /// <returns>A new session instance.</returns>
    ValueTask<IDatabaseSession> CreateSessionAsync(CancellationToken cancellationToken = default);
}

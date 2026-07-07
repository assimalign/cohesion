using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace Assimalign.Cohesion.Database.Blob;

/// <summary>
/// Represents a blob-model database: named containers of streamed large objects
/// with a transactional metadata catalog.
/// </summary>
public interface IBlobDatabase : IDatabase
{
    /// <summary>
    /// Creates a new blob container.
    /// </summary>
    /// <param name="name">The name of the container to create.</param>
    /// <param name="cancellationToken">Cancellation token for the operation.</param>
    /// <returns>The created container.</returns>
    /// <exception cref="DatabaseException">Thrown when a container with the same name already exists.</exception>
    ValueTask<IBlobContainer> CreateContainerAsync(string name, CancellationToken cancellationToken = default);

    /// <summary>
    /// Opens an existing blob container.
    /// </summary>
    /// <param name="name">The name of the container to open.</param>
    /// <param name="cancellationToken">Cancellation token for the operation.</param>
    /// <returns>The opened container.</returns>
    /// <exception cref="DatabaseException">Thrown when the container does not exist.</exception>
    ValueTask<IBlobContainer> GetContainerAsync(string name, CancellationToken cancellationToken = default);

    /// <summary>
    /// Drops a blob container and its blobs.
    /// </summary>
    /// <param name="name">The name of the container to drop.</param>
    /// <param name="cancellationToken">Cancellation token for the operation.</param>
    /// <exception cref="DatabaseException">Thrown when the container does not exist.</exception>
    ValueTask DropContainerAsync(string name, CancellationToken cancellationToken = default);

    /// <summary>
    /// Enumerates the containers in this database.
    /// </summary>
    /// <param name="cancellationToken">Cancellation token for the operation.</param>
    /// <returns>An async sequence of containers.</returns>
    IAsyncEnumerable<IBlobContainer> GetContainersAsync(CancellationToken cancellationToken = default);
}

using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace Assimalign.Cohesion.Database.Documents;

/// <summary>
/// Represents a document-model database: named collections of versioned documents.
/// </summary>
public interface IDocumentDatabase : IDatabase
{
    /// <summary>
    /// Creates a new document collection.
    /// </summary>
    /// <param name="name">The name of the collection to create.</param>
    /// <param name="cancellationToken">Cancellation token for the operation.</param>
    /// <returns>The created collection.</returns>
    /// <exception cref="DatabaseException">Thrown when a collection with the same name already exists.</exception>
    ValueTask<IDocumentCollection> CreateCollectionAsync(string name, CancellationToken cancellationToken = default);

    /// <summary>
    /// Opens an existing document collection.
    /// </summary>
    /// <param name="name">The name of the collection to open.</param>
    /// <param name="cancellationToken">Cancellation token for the operation.</param>
    /// <returns>The opened collection.</returns>
    /// <exception cref="DatabaseException">Thrown when the collection does not exist.</exception>
    ValueTask<IDocumentCollection> GetCollectionAsync(string name, CancellationToken cancellationToken = default);

    /// <summary>
    /// Drops a document collection and its documents and indexes.
    /// </summary>
    /// <param name="name">The name of the collection to drop.</param>
    /// <param name="cancellationToken">Cancellation token for the operation.</param>
    /// <exception cref="DatabaseException">Thrown when the collection does not exist.</exception>
    ValueTask DropCollectionAsync(string name, CancellationToken cancellationToken = default);

    /// <summary>
    /// Enumerates the collections in this database.
    /// </summary>
    /// <param name="cancellationToken">Cancellation token for the operation.</param>
    /// <returns>An async sequence of collections.</returns>
    IAsyncEnumerable<IDocumentCollection> GetCollectionsAsync(CancellationToken cancellationToken = default);
}

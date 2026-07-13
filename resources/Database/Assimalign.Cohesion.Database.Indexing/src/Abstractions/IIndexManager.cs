using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

using Assimalign.Cohesion.Database.Transactions;

namespace Assimalign.Cohesion.Database.Indexing;

/// <summary>
/// Creates, opens, and drops the indexes belonging to one logical database.
/// </summary>
public interface IIndexManager
{
    /// <summary>
    /// Creates a new index on the specified object.
    /// </summary>
    /// <param name="transaction">The transaction the DDL operation belongs to.</param>
    /// <param name="objectId">The identity of the object (table, collection, container) being indexed.</param>
    /// <param name="definition">The definition of the index to create.</param>
    /// <param name="cancellationToken">Cancellation token for the operation.</param>
    /// <returns>The created index.</returns>
    /// <exception cref="IndexException">Thrown when an index with the same name already exists on the object.</exception>
    ValueTask<IIndex> CreateIndexAsync(ITransactionContext transaction, ulong objectId, IndexDefinition definition, CancellationToken cancellationToken = default);

    /// <summary>
    /// Drops an index from the specified object.
    /// </summary>
    /// <param name="transaction">The transaction the DDL operation belongs to.</param>
    /// <param name="objectId">The identity of the object the index belongs to.</param>
    /// <param name="name">The name of the index to drop.</param>
    /// <param name="cancellationToken">Cancellation token for the operation.</param>
    /// <exception cref="IndexException">Thrown when the index does not exist.</exception>
    ValueTask DropIndexAsync(ITransactionContext transaction, ulong objectId, string name, CancellationToken cancellationToken = default);

    /// <summary>
    /// Opens an existing index by name.
    /// </summary>
    /// <param name="objectId">The identity of the object the index belongs to.</param>
    /// <param name="name">The name of the index.</param>
    /// <param name="index">When this method returns true, the opened index.</param>
    /// <returns>True when the index exists; otherwise false.</returns>
    bool TryGetIndex(ulong objectId, string name, out IIndex index);

    /// <summary>
    /// Enumerates the indexes defined on the specified object.
    /// </summary>
    /// <param name="objectId">The identity of the object.</param>
    /// <returns>The indexes defined on the object.</returns>
    IReadOnlyList<IIndex> GetIndexes(ulong objectId);
}

using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

using Assimalign.Cohesion.Database.Storage;
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

    /// <summary>
    /// Purges the given writers' stamps out of every live index in one walk per
    /// tree: entries the writers inserted are physically removed and tombstones
    /// they stamped are cleared. This is the open-time recovery obligation — the
    /// journal cannot prove these writers committed, the in-memory undo ledger
    /// died with the process, and snapshots have no commit-log awareness, so an
    /// unproven writer's stamps must not remain visible-by-default in any index.
    /// Idempotent: re-running after a crash mid-purge removes what remains.
    /// </summary>
    /// <param name="transaction">The physical storage bracket the purge rides.</param>
    /// <param name="writers">The transaction sequences the journal cannot prove committed.</param>
    /// <param name="cancellationToken">Cancellation token for the operation.</param>
    /// <returns>The number of entries removed or restored.</returns>
    ValueTask<long> PurgeWritersAsync(IStorageTransaction transaction, IReadOnlySet<TransactionSequence> writers, CancellationToken cancellationToken = default);
}

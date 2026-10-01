using System.Linq;
using System.Threading;
using System.Threading.Tasks;

using Assimalign.Cohesion.Database.Indexing;
using Assimalign.Cohesion.Database.Sql.Catalog;
using Assimalign.Cohesion.Database.Storage;
using Assimalign.Cohesion.Database.Transactions;

namespace Assimalign.Cohesion.Database.Sql.Internal;

internal sealed partial class SqlPlanExecutor
{
    /// <summary>
    /// Replaces one attached index's tree with a fresh tree built from every
    /// stored row version under the current key encoding — the open-time
    /// key-format upgrade (data-storage format 3 → 4, #1099). The build mirrors
    /// CREATE INDEX: each version's entry keeps the version's original writer and
    /// deleter stamps, so every snapshot reads exactly what the row scan shows it.
    /// </summary>
    /// <remarks>
    /// The old tree only leaves the manager's directory; its pages are untouched
    /// and await vacuum, as after DROP INDEX. Until the caller persists the new
    /// registration, the catalog still names the old root, so a crash re-attaches
    /// the old tree and the upgrade runs again. No uniqueness check runs: rows
    /// that the previous encoding kept apart but SQL equality joins (equal ticks
    /// with different kinds, one instant at different offsets) are carried into
    /// the rebuilt tree as they are, because failing the open would leave the
    /// data unreachable. The rebuilt tree's latest-state check rejects every
    /// further equal value.
    /// </remarks>
    /// <param name="transaction">The upgrade's transaction; the manager resolves its statement bracket.</param>
    /// <param name="bracket">The durable statement bracket the new tree's pages ride.</param>
    /// <param name="table">The table the index belongs to.</param>
    /// <param name="metadata">The catalog description of the index to rebuild.</param>
    /// <param name="cancellationToken">Cancellation token for the operation.</param>
    /// <returns><see langword="true"/> when a tree was rebuilt; <see langword="false"/> when none was attached.</returns>
    internal async ValueTask<bool> RebuildIndexAsync(
        ITransactionContext transaction,
        IStorageTransaction bracket,
        SqlCatalogTable table,
        SqlCatalogIndex metadata,
        CancellationToken cancellationToken)
    {
        if (!_indexManager.TryGetIndex(table.ObjectId, metadata.Name, out var current))
        {
            // A description without an attached tree is the torn state seeks
            // already tolerate by scanning; there is nothing to re-encode.
            return false;
        }

        var definition = new IndexDefinition(current.Name, current.Kind, current.IsUnique);
        await _indexManager.DropIndexAsync(transaction, table.ObjectId, current.Name, cancellationToken).ConfigureAwait(false);
        var rebuilt = await _indexManager.CreateIndexAsync(transaction, table.ObjectId, definition, cancellationToken).ConfigureAwait(false);
        int[] ordinals = metadata.ColumnNames.Select(column => FindColumnOrdinal(table, column)).ToArray();

        foreach (var version in ScanVersions(table, cancellationToken))
        {
            await rebuilt.InsertVersionAsync(
                bracket,
                BuildIndexKey(table, ordinals, version.Values),
                SqlRecordLocation.Pack(version.Location.PageId, version.Location.SlotIndex),
                version.Writer,
                version.Deleter,
                cancellationToken).ConfigureAwait(false);
        }

        return true;
    }
}

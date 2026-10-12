using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

using Assimalign.Cohesion.Database.Indexing;
using Assimalign.Cohesion.Database.Sql.Catalog;
using Assimalign.Cohesion.Database.Transactions;

namespace Assimalign.Cohesion.Database.Sql.Internal;

internal sealed partial class SqlPlanExecutor
{
    private async Task BuildDeferredIndexAsync(SqlCatalogTable table, IndexDefinition definition,
        int[] ordinals, SqlStatementContext statement, Action created, CancellationToken cancellationToken)
    {
        // Counting is worker work too. Build only checks owner allocation metadata.
        long total = 0;
        foreach (var row in ScanVersions(table, cancellationToken))
        {
            total++;
        }
        statement.ProvisioningProgress!(0, total);
        var index = await statement.Coordinator.ApplyStatementAsync(statement.Transaction,
            async bracket => await _indexManager.CreateIndexAsync(statement.Transaction, table.ObjectId, definition, cancellationToken).ConfigureAwait(false),
            durable: true, cancellationToken).ConfigureAwait(false);
        created();
        var keys = definition.IsUnique ? new HashSet<string>(StringComparer.Ordinal) : null;
        long processed = 0;

        // Each bracket is bounded; no table-sized storage gate prevents another table
        // from committing. The table lock and snapshot pin outlive every bracket.
        foreach (var batch in ScanVersions(table, cancellationToken).Chunk(64))
        {
            await statement.Coordinator.ApplyStatementAsync(statement.Transaction, async bracket =>
            {
                foreach (var version in batch)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    var key = BuildIndexKey(table, ordinals, version.Values);
                    if (keys is not null && version.Deleter == TransactionSequence.None && !keys.Add(Convert.ToHexString(key.Encoded.Span)))
                    {
                        throw new SqlConstraintViolationException(definition.Name, $"{table.Schema}.{table.Name}", "UNIQUE");
                    }
                    await index.InsertVersionAsync(bracket, key, SqlRecordLocation.Pack(version.Location.PageId, version.Location.SlotIndex),
                        version.Writer, version.Deleter, cancellationToken).ConfigureAwait(false);
                }
                return true;
            }, durable: true, cancellationToken).ConfigureAwait(false);
            processed += batch.Length;
            statement.ProvisioningProgress(processed, Math.Max(processed, total));
        }
        statement.ProvisioningProgress(processed, processed);
    }

    // Unpublished trees are never exported by a checkpoint or by unrelated DDL.
    // A crash discards their directory entries; the pending operation rebuilds them.
    // This snapshot may miss a concurrent publication. The catalog merges only
    // this statement's added descriptions under its publication lock.
    private IReadOnlyList<BTreeIndexRegistration> PublishedRegistrations(IReadOnlyList<SqlCatalogIndex> added)
        => _indexManager.ExportRegistrations().Where(registration =>
            _catalog.TryGetIndex(registration.ObjectId, registration.Definition.Name, out _) ||
            added.Any(index => index.TableObjectId == registration.ObjectId &&
                string.Equals(index.Name, registration.Definition.Name, StringComparison.OrdinalIgnoreCase))).ToArray();
}

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
    internal static Action<string, long>? RewriteStage { get; set; }

    // Physical compaction and ALTER TYPE share this copier and publication path.
    internal async Task RewriteTableAsync(SqlCatalogTable table, SqlStatementContext statement,
        CancellationToken cancellationToken, Func<SqlCatalogTable, ulong, SqlCatalogTable>? describe = null,
        Func<object?[], object?[]>? transform = null, Action<SqlCatalogTable, SqlCatalogIndex[]>? validate = null, bool definitionChange = false)
    {
        // Lock/latch order: fail-fast schema admission -> old table X (before queueing)
        // -> fail-fast metadata admission (on the worker) -> reserved table X
        // -> coordinator apply gate -> index directory/tree latch/page pins. EnterMetadata
        // must not wait under old table X: definition DDL may own metadata while waiting
        // for that table. No apply gate
        // or page/tree latch spans catalog publication. The snapshot gate precedes the
        // catalog monitor; catalog storage brackets are innermost. The statement epoch
        // and logical transaction pin outlive all copying, directory cleanup and undo.
        // A source scan may retain a source-page pin (no latch) before target apply;
        // source and target owners are disjoint and reclamation waits for these pins.
        await statement.Coordinator.LockManager.AcquireAsync(statement.DdlLockOwner,
            LockResource.Object(table.ObjectId), LockMode.Exclusive, cancellationToken).ConfigureAwait(false);
        // Exact description identity also refuses a metadata edit with unchanged owner id.
        if (!_catalog.TryGetTable(table.Schema, table.Name, out var current) || !ReferenceEquals(current, table))
        {
            throw new SqlCatalogException("CS04064", string.Format(System.Globalization.CultureInfo.CurrentCulture,
                ErrorMessages.TableIdentityReplaced, table.Schema, table.Name));
        }
        describe ??= static (previous, id) => new SqlCatalogTable(id, previous.Schema, previous.Name,
            previous.Columns, previous.PrimaryKeyColumns, previous.Owner, previous.OwningSchema, previous.Constraints);
        await ReplaceTableStorageAsync([table], null, describe, transform, copyRows: true, restartIdentity: false,
            SqlObjectIdentity.Table(table.Schema, table.Name), statement, cancellationToken, validate, definitionChange).ConfigureAwait(false);
    }

    private async Task ReplaceTableStorageAsync(SqlCatalogTable[] previous, IReadOnlyList<SqlCatalogTable>? named,
        Func<SqlCatalogTable, ulong, SqlCatalogTable>? describe, Func<object?[], object?[]>? transform,
        bool copyRows, bool restartIdentity, string actualObject, SqlStatementContext statement, CancellationToken cancellationToken,
        Action<SqlCatalogTable, SqlCatalogIndex[]>? validate = null, bool definitionChange = false)
    {
        var oldIndexes = previous.SelectMany(table => _catalog.GetIndexes(table.ObjectId)).ToArray();
        var retiredIndexes = new Dictionary<SqlCatalogIndex, BTreeIndex>(ReferenceEqualityComparer.Instance);
        foreach (var index in oldIndexes)
        {
            if (_indexManager.TryGetIndex(index.TableObjectId, index.Name, out var tree)) { retiredIndexes.Add(index, tree); }
        }
        var replacements = _catalog.ReserveStorageReplacement(previous, named, describe, cancellationToken);
        var mapping = previous.Select((table, index) => (table.ObjectId, Replacement: replacements[index]))
            .ToDictionary(pair => pair.ObjectId, pair => pair.Replacement);
        var indexes = oldIndexes.Select(index => new SqlCatalogIndex(mapping[index.TableObjectId].ObjectId,
            index.Name, index.ColumnNames, index.IsUnique, index.Owner, index.OwningSchema, index.IsPrimaryKey)).ToArray();
        var created = new List<SqlCatalogIndex>();
        bool published = false;
        Exception? operationFailure = null;
        try
        {
            if (copyRows) { RewriteStage?.Invoke("Reserved", checked((long)replacements[0].ObjectId)); }
            else { TruncateReserved?.Invoke(); }
            foreach (var table in replacements)
            {
                await statement.Coordinator.LockManager.AcquireAsync(statement.DdlLockOwner,
                    LockResource.Object(table.ObjectId), LockMode.Exclusive, cancellationToken).ConfigureAwait(false);
                _definitions.Get(table, cancellationToken);
            }
            if (indexes.Length != 0)
            {
                await statement.Coordinator.ApplyStatementAsync(statement.Transaction, async _ =>
                {
                    foreach (var index in indexes)
                    {
                        await _indexManager.CreateIndexAsync(statement.Transaction, index.TableObjectId,
                            new IndexDefinition(index.Name, IndexKind.BTree, index.IsUnique), cancellationToken).ConfigureAwait(false);
                        created.Add(index);
                    }
                    return true;
                }, durable: true, cancellationToken).ConfigureAwait(false);
            }
            if (copyRows)
            {
                await CopyReplacementRowsAsync(previous[0], replacements[0], indexes, statement, transform, cancellationToken).ConfigureAwait(false);
                validate?.Invoke(replacements[0], indexes);
                RewriteStage?.Invoke("Prepared", 0);
            }
            else { TruncatePrepared?.Invoke(); }
            await _catalog.TruncateSnapshotGate.WaitAsync(cancellationToken).ConfigureAwait(false);
            TransactionContext? marker = null;
            try
            {
                marker = await statement.Coordinator.BeginSnapshotPinAsync(cancellationToken).ConfigureAwait(false);
                await _catalog.PublishStorageReplacementAsync(previous, named, replacements, indexes, PublishedRegistrations(indexes),
                    retiredIndexes, restartIdentity, marker.Sequence,
                    actualObject,
                    statement.ScriptReceipt, cancellationToken, definitionChange).ConfigureAwait(false);
                // Set before any observer can throw: publication is irrevocable, and cleanup
                // must remove old handles even when cancellation or a test observer intervenes.
                published = true;
                statement.Metrics.RecordSelfCommit();
                if (copyRows) { RewriteStage?.Invoke("Published", 0); }
                else { TruncatePublished?.Invoke(); }
            }
            finally
            {
                try
                {
                    if (marker is not null)
                    {
                        await statement.Coordinator.RollbackAsync(marker, CancellationToken.None).ConfigureAwait(false);
                        if (published) { _catalog.ReleaseTruncatePublication(previous); }
                    }
                }
                finally { _catalog.TruncateSnapshotGate.Release(); }
            }
            AfterSelfCommit?.Invoke();
        }
        catch (Exception failure)
        {
            // Preserve the original coded storage/cancellation failure if mandatory
            // directory cleanup also fails; neither outcome may hide the other.
            operationFailure = failure;
            throw;
        }
        finally
        {
            try
            {
                await RemoveDroppedIndexesAsync(statement, published ? oldIndexes : created,
                    cancellationToken: CancellationToken.None).ConfigureAwait(false);
            }
            catch (Exception cleanupFailure) when (operationFailure is not null)
            {
                throw new AggregateException(operationFailure, cleanupFailure);
            }
        }
    }

    private async Task CopyReplacementRowsAsync(SqlCatalogTable previous, SqlCatalogTable replacement,
        SqlCatalogIndex[] descriptions, SqlStatementContext statement, Func<object?[], object?[]>? transform,
        CancellationToken cancellationToken)
    {
        var indexes = descriptions.Select(description =>
        {
            _indexManager.TryGetIndex(description.TableObjectId, description.Name, out var index);
            return (Index: index, Ordinals: description.ColumnNames.Select(name => FindColumnOrdinal(replacement, name)).ToArray());
        }).ToArray();
        var snapshot = ConstraintCurrentSnapshot(statement);
        long processed = 0;
        statement.ProvisioningProgress?.Invoke(0, null);
        // Table X has waited for all writers and their undo. Older snapshots use the old
        // owner, so only currently live versions need copying; preserve their committed
        // writer stamps. A prepared owner's intent remains durable until publication.
        // The scan retains one source-page pin across chunks, with no page latch.
        // Target writes use a different owner; decoded-row retention is bounded to 64.
        foreach (var batch in ScanVersions(previous, cancellationToken)
            .Where(version => snapshot.IsVisible(version.Writer) &&
                (version.Deleter == TransactionSequence.None || !snapshot.IsVisible(version.Deleter))).Chunk(64))
        {
            await statement.Coordinator.ApplyStatementAsync(statement.Transaction, async bracket =>
            {
                foreach (var version in batch)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    var values = transform is null ? version.Values : transform(version.Values);
                    byte[] record = SqlRowCodec.Encode(replacement, values, version.Writer);
                    var location = _storage.InsertRow(bracket, replacement.ObjectId, record);
                    foreach (var index in indexes)
                    {
                        await index.Index.InsertVersionAsync(bracket, BuildIndexKey(replacement, index.Ordinals, values),
                            SqlRecordLocation.Pack(location.PageId, location.SlotIndex), version.Writer,
                            TransactionSequence.None, cancellationToken).ConfigureAwait(false);
                    }
                }
                return true;
            }, durable: true, cancellationToken).ConfigureAwait(false);
            processed += batch.Length;
            statement.ProvisioningProgress?.Invoke(processed, null);
            RewriteStage?.Invoke("Copied", processed);
        }
        foreach (var index in indexes)
        {
            await ValidateBuiltUniqueIndexAsync(replacement, index.Index, statement, cancellationToken).ConfigureAwait(false);
        }
        statement.ProvisioningProgress?.Invoke(processed, processed);
    }
}

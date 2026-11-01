using System;
using System.Collections.Generic;
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
    internal static Action<string, long>? RewriteStage { get; set; }

    // Physical compaction and ALTER TYPE share this copier and publication path.
    internal async Task RewriteTableAsync(SqlCatalogTable table, SqlStatementContext statement,
        CancellationToken cancellationToken, Func<SqlCatalogTable, ulong, SqlCatalogTable>? describe = null,
        Func<object?[], object?[]>? transform = null, Action<SqlCatalogTable, SqlCatalogIndex[]>? validate = null, bool definitionChange = false, IReadOnlyList<SqlCatalogIndex>? additionalIndexes = null)
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
        var ownedGrants = await LockOwnedSequencesAsync([table], statement, cancellationToken).ConfigureAwait(false);
        await statement.Coordinator.LockManager.AcquireAsync(statement.DdlLockOwner,
            LockResource.Object(table.ObjectId), LockMode.Exclusive, cancellationToken).ConfigureAwait(false);
        VerifyOwnedSequences([table], ownedGrants, statement);
        // Exact description identity also refuses a metadata edit with unchanged owner id.
        if (!_catalog.TryGetTable(table.Schema, table.Name, out var current) || !ReferenceEquals(current, table))
        {
            throw new SqlCatalogException("CS04064", string.Format(System.Globalization.CultureInfo.CurrentCulture,
                ErrorMessages.TableIdentityReplaced, table.Schema, table.Name));
        }
        describe ??= static (previous, id) => new SqlCatalogTable(id, previous.Schema, previous.Name,
            previous.Columns, previous.PrimaryKeyColumns, previous.Owner, previous.OwningSchema, previous.Constraints);
        await ReplaceTableStorageAsync([table], null, describe, transform, copyRows: true, restartIdentity: false,
            SqlObjectIdentity.Table(table.Schema, table.Name), statement, cancellationToken, validate, definitionChange, additionalIndexes).ConfigureAwait(false);
    }

    private async Task ReplaceTableStorageAsync(SqlCatalogTable[] previous, IReadOnlyList<SqlCatalogTable>? named,
        Func<SqlCatalogTable, ulong, SqlCatalogTable>? describe, Func<object?[], object?[]>? transform,
        bool copyRows, bool restartIdentity, string actualObject, SqlStatementContext statement, CancellationToken cancellationToken,
        Action<SqlCatalogTable, SqlCatalogIndex[]>? validate = null, bool definitionChange = false, IReadOnlyList<SqlCatalogIndex>? additionalIndexes = null)
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
            index.Name, index.ColumnNames, index.IsUnique, index.Owner, index.OwningSchema, index.IsPrimaryKey, index.Predicate, index.PredicateColumns, index.PredicateFunctionCalls, index.KeyOrders)).ToArray();
        if (additionalIndexes is { Count: > 0 })
        { indexes = indexes.Concat(additionalIndexes.Select(index => new SqlCatalogIndex(replacements[0].ObjectId, index.Name, index.ColumnNames, index.IsUnique, index.Owner, index.OwningSchema, index.IsPrimaryKey, index.Predicate, index.PredicateColumns, index.PredicateFunctionCalls, index.KeyOrders))).ToArray(); }
        var created = new List<SqlCatalogIndex>();
        bool published = false;
        Exception? operationFailure = null;
        try
        {
            if (copyRows) { RewriteStage?.Invoke("Reserved", checked((long)replacements[0].ObjectId)); }
            else { TruncateReserved?.Invoke(); }
            foreach (var table in replacements)
            {
                // Newly reserved sequences are unreachable through either SQL name/id
                // lookup. Taking their X grant here cannot wait for another session.
                foreach (var column in copyRows ? table.Columns.Where(column => column.Identity is not null && previous[0].FindColumn(column.Name)?.Identity is null) : Enumerable.Empty<SqlCatalogColumn>())
                {
                    var sequence = _catalog.IdentitySequence(table.ObjectId, column.Name);
                    await statement.Coordinator.LockManager.AcquireAsync(statement.DdlLockOwner,
                        LockResource.Object(sequence.ObjectId), LockMode.Exclusive, cancellationToken).ConfigureAwait(false);
                    SqlSequenceExecution.CheckGrant(statement, statement.DdlLockOwner);
                }
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
            finally
            {
                if (!published) { _catalog.AbandonStorageReplacement(replacements); }
            }
        }
    }

    private async Task CopyReplacementRowsAsync(SqlCatalogTable previous, SqlCatalogTable replacement,
        SqlCatalogIndex[] descriptions, SqlStatementContext statement, Func<object?[], object?[]>? transform,
        CancellationToken cancellationToken, long copyRows = 0)
    {
        var indexes = descriptions.Select(description =>
        {
            _indexManager.TryGetIndex(description.TableObjectId, description.Name, out var index);
            if (description.Predicate is not null) { _definitions.GetIndexPredicate(replacement, description, cancellationToken); }
            return (Metadata: description, Index: index, Ordinals: description.ColumnNames.Select(name => FindColumnOrdinal(replacement, name)).ToArray());
        }).ToArray();
        var snapshot = ConstraintCurrentSnapshot(statement);
        long processed = 0;
        statement.ProvisioningProgress?.Invoke(0, null);
        // Table X has waited for all writers and their undo. Older snapshots use the old
        // owner, so only currently live versions need copying; preserve their committed
        // writer stamps. A prepared owner's intent remains durable until publication.
        // The scan retains one source-page pin across chunks, with no page latch.
        // Target writes use a different owner; decoded-row retention is bounded to 64.
        using var scan = ScanVersions(previous, cancellationToken, statement.Metrics)
            .Where(version => snapshot.IsVisible(version.Writer) &&
                (version.Deleter == TransactionSequence.None || !snapshot.IsVisible(version.Deleter))).GetEnumerator();
        while (scan.MoveNext())
        {
            using var memory = new SqlDmlMemory(statement.Metrics, _definitions.StatementMemoryBudget);
            // Tuple slots and conversion-array references are retained together. Charge
            // before growing the chunk, then charge each decoded row before retaining it.
            memory.Reserve(80L + 64L * 48);
            var batch = new List<((PageId PageId, int SlotIndex) Location, object?[] Values,
                TransactionSequence Writer, TransactionSequence Deleter)>(64);
            do
            {
                memory.Retain(scan.Current.Values);
                batch.Add(scan.Current);
            } while (batch.Count < 64 && scan.MoveNext());
            // User callbacks and constraint evaluation must not hold the database-wide
            // apply gate. Prepare this bounded chunk as ordinary DML does, then apply
            // storage/index mutations only. The source table X keeps its rows stable.
            var converted = new object?[batch.Count][];
            for (int i = 0; i < batch.Count; i++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                converted[i] = transform is null ? batch[i].Values : transform(batch[i].Values);
                memory.Retain(converted[i]);
                foreach (var index in indexes) { MatchesIndex(replacement, index.Metadata, converted[i], cancellationToken: cancellationToken); }
            }
            long journalStart = 0;
            statement.CheckCopySpace(SqlSchemaProvisioner.JointCopyTouches(batch.Count, copyRows, indexes.Length),
                SqlSchemaProvisioner.JointCopyAllocations(batch.Count, copyRows, indexes.Length));
            await statement.Coordinator.ApplyStatementAsync(statement.Transaction, async bracket =>
            {
                statement.CheckCopySpace(SqlSchemaProvisioner.JointCopyTouches(batch.Count, copyRows, indexes.Length),
                    SqlSchemaProvisioner.JointCopyAllocations(batch.Count, copyRows, indexes.Length));
                journalStart = _storage.Journal.Length - 38; // Include this bracket's already appended Begin frame.
                for (int i = 0; i < batch.Count; i++)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    var version = batch[i];
                    var values = converted[i];
                    byte[] record = SqlRowCodec.Encode(replacement, values, version.Writer);
                    long recordBytes = (24L + record.Length + 7) & ~7L;
                    memory.Reserve(recordBytes);
                    try
                    {
                        var location = _storage.InsertRow(bracket, replacement.ObjectId, record);
                        foreach (var index in indexes)
                        {
                            if (!MatchesIndex(replacement, index.Metadata, values, prepared: true)) { continue; }
                            await index.Index.InsertVersionAsync(bracket, BuildIndexKey(replacement, index.Ordinals, values, index.Metadata.KeyOrders),
                                SqlRecordLocation.Pack(location.PageId, location.SlotIndex), version.Writer,
                                TransactionSequence.None, cancellationToken).ConfigureAwait(false);
                        }
                    }
                    finally { memory.Release(recordBytes); }
                }
                return true;
            }, durable: true, cancellationToken).ConfigureAwait(false);
            await CheckpointReplacementCopyAsync(statement, journalStart, cancellationToken).ConfigureAwait(false);
            ForgetIndexRows(converted);
            processed += batch.Count;
            statement.ProvisioningProgress?.Invoke(processed, null);
            RewriteStage?.Invoke("Copied", processed);
        }
        foreach (var index in indexes)
        {
            await ValidateBuiltUniqueIndexAsync(replacement, index.Index, statement, cancellationToken, index.Metadata.Predicate is null ? null : index.Ordinals, index.Metadata.Predicate is not null).ConfigureAwait(false);
        }
        statement.ProvisioningProgress?.Invoke(processed, processed);
    }

    private async Task CheckpointReplacementCopyAsync(SqlStatementContext statement, long journalStart, CancellationToken token)
    {
        if (!statement.CheckpointCopy) { return; }
        // Sample at commit, before checkpoint truncation; include only deltas across
        // our brackets, never untouched writers' WAL accumulated between brackets.
        long length = _storage.Journal.Length;
        long added = statement.AddCopyJournalBytes(length - journalStart);
        RewriteStage?.Invoke("CopyCommitted", length);
        if (added < SqlSchemaProvisioner.JointCopyCheckpointBytes) { return; }
        while (true)
        {
            token.ThrowIfCancellationRequested();
            try { statement.Coordinator.Checkpoint(token); break; }
            catch (StorageTransactionException busy) when (busy.Code != StorageTransactionErrorCode.ForeignStorage)
            {
                // Reclamation uses bounded storage brackets outside the apply gate.
                // Like the engine checkpoint worker, retry busy; never skip the bound.
                RewriteStage?.Invoke("CheckpointBusy", length);
                await Task.Delay(1, token).ConfigureAwait(false);
            }
        }
        statement.ResetCopyJournalBytes();
        RewriteStage?.Invoke("CopyCheckpointed", length);
    }
}

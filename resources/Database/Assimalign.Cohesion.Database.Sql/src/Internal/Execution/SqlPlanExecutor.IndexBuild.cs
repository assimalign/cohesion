using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

using Assimalign.Cohesion.Database.Indexing;
using Assimalign.Cohesion.Database.Sql.Catalog;

namespace Assimalign.Cohesion.Database.Sql.Internal;

internal sealed partial class SqlPlanExecutor
{
    // Test-only observation; null in production. Observers retain weak references only.
    internal static Action<object?[]>? RowDecoded { get; set; }
    internal static Action<IndexKey>? IndexKeyBuilt { get; set; }

    // Test seam: sample retained workspace while the validation cursor is still walking.
    internal static Action? UniqueIndexEntryValidated { get; set; }

    private async Task BuildDeferredIndexAsync(SqlCatalogTable table, IndexDefinition definition,
        int[] ordinals, SqlStatementContext statement, Action created, CancellationToken cancellationToken, SqlCatalogIndex? metadata = null,
        ulong physicalOwner = 0)
    {
        // Counting is worker work too. Build only checks owner allocation metadata.
        long total = 0;
        foreach (var row in ScanVersions(table, cancellationToken))
        {
            total++;
        }
        statement.ProvisioningProgress?.Invoke(0, total);
        statement.ObserveCopyJournalStart(_storage.JournalLength);
        statement.CheckCopySpace(1);
        long journalStart = 0;
        var index = await statement.Coordinator.ApplyStatementAsync(statement.Transaction,
            async bracket =>
            {
                statement.ObserveCopyJournalStart(_storage.JournalLength - 38);
                statement.CheckCopySpace(1);
                long allocationStart = statement.CaptureCopyAllocations();
                journalStart = _storage.JournalLength - 38;
                var createdIndex = physicalOwner == 0
                    ? await _indexManager.CreateIndexAsync(statement.Transaction, table.ObjectId, definition, cancellationToken).ConfigureAwait(false)
                    : await _indexManager.CreatePreparedAsync(statement.Transaction, table.ObjectId, physicalOwner, definition, cancellationToken).ConfigureAwait(false);
                // Track registration before the bracket's durable end can fail.
                created();
                statement.AddCopyAllocations(allocationStart);
                return createdIndex;
            },
            durable: true, cancellationToken).ConfigureAwait(false);
        await CheckpointReplacementCopyAsync(statement, journalStart, cancellationToken).ConfigureAwait(false);
        // A root never moves on splits. Capture once, outside the row-bracket loop;
        // per-bracket costing must not walk/allocate the whole database directory.
        long root = statement.CheckpointCopy ? _indexManager.ExportRegistrations(includePrepared: true)
            .Single(value => value.ObjectId == table.ObjectId && value.Definition.Name == definition.Name).RootPageId : 0;
        long processed = 0;
        int maximumKey = 0;

        // Each bracket is bounded; no table-sized storage gate prevents another table
        // from committing. The table lock and snapshot pin outlive every bracket.
        foreach (var batch in ScanVersions(table, cancellationToken).Chunk(64))
        {
            var included = metadata?.Predicate is null ? batch : batch.Where(version => MatchesIndex(table, metadata, version.Values, cancellationToken: cancellationToken, record: false)).ToArray();
            long touches = 0, allocations = 0;
            if (statement.CheckpointCopy)
            {
                foreach (var version in included)
                { maximumKey = Math.Max(maximumKey, BuildIndexKey(table, ordinals, version.Values, metadata?.KeyOrders).Length); }
                int height = SqlSchemaProvisioner.JointTreeHeight(_storage, root, physicalOwner == 0 ? table.ObjectId : physicalOwner, out int free);
                int entryBytes = maximumKey + 28;
                if (free < included.Length * entryBytes) { height += SqlSchemaProvisioner.JointTreeGrowth(included.Length, entryBytes); }
                touches = included.Length * (4L * height + 3);
                allocations = included.Length * 2L * height;
                statement.ObserveCopyJournalStart(_storage.JournalLength);
                statement.CheckCopySpace(touches, allocations);
            }
            await statement.Coordinator.ApplyStatementAsync(statement.Transaction, async bracket =>
            {
                statement.ObserveCopyJournalStart(_storage.JournalLength - 38);
                statement.CheckCopySpace(touches, allocations);
                long allocationStart = statement.CaptureCopyAllocations();
                journalStart = _storage.JournalLength - 38;
                foreach (var version in included)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    var key = BuildIndexKey(table, ordinals, version.Values, metadata?.KeyOrders);
                    await index.InsertVersionAsync(bracket, key, SqlRecordLocation.Pack(version.Location.PageId, version.Location.SlotIndex),
                        version.Writer, version.Deleter, cancellationToken).ConfigureAwait(false);
                }
                statement.AddCopyAllocations(allocationStart);
                return true;
            }, durable: true, cancellationToken).ConfigureAwait(false);
            await CheckpointReplacementCopyAsync(statement, journalStart, cancellationToken).ConfigureAwait(false);
            processed += batch.Length;
            statement.ProvisioningProgress?.Invoke(processed, Math.Max(processed, total));
        }
        await ValidateBuiltUniqueIndexAsync(table, index, statement, cancellationToken, metadata?.Predicate is null ? null : ordinals, metadata?.Predicate is not null).ConfigureAwait(false);
        statement.ProvisioningProgress?.Invoke(processed, processed);
    }

    private async ValueTask ValidateBuiltUniqueIndexAsync(SqlCatalogTable table, BTreeIndex index,
        SqlStatementContext statement, CancellationToken cancellationToken, int[]? valueOrdinals = null, bool nullsDistinct = false)
    {
        if (!index.IsUnique) { return; }

        // Lock/latch order: the caller holds table X before any apply gate, then the
        // cursor takes the tree read latch and page pins for one bounded batch. It
        // takes no key locks and retains no latch across a move or disposal. Table X
        // outlives validation and publication, so latest-state visibility is stable
        // even when the DDL transaction's original snapshot predates live writers.
        // All versions remain in the tree for older readers; only live keys compare.
        await using var cursor = index.OpenCursor(ConstraintCurrentSnapshot(statement), IndexKeyRange.All,
            cancellationToken: cancellationToken);
        IndexKey? previous = null;
        while (await cursor.MoveNextAsync(cancellationToken).ConfigureAwait(false))
        {
            var key = cursor.CurrentKey;
            if (previous is { } last && last.Encoded.Span.SequenceEqual(key.Encoded.Span))
            {
                object? value = null;
                if (valueOrdinals is not null)
                {
                    // Preserve CREATE INDEX's safe scalar diagnostic without retaining
                    // the build's rows or decoding any row on the successful path.
                    var (pageId, slotIndex) = SqlRecordLocation.Unpack(cursor.CurrentEntryReference);
                    if (_storage.TryReadRecord(pageId, slotIndex, table.ObjectId, out var record))
                    {
                        var values = DecodeRow(record, pageId, slotIndex, table,
                            _definitions.Get(table, cancellationToken).MissingValues, out _, out _);
                        if (nullsDistinct && valueOrdinals.Any(ordinal => values[ordinal] is null)) { continue; }
                        value = SafeValue(valueOrdinals.Select(ordinal => values[ordinal]).ToArray());
                    }
                }
                throw new SqlConstraintViolationException(index.Name, $"{table.Schema}.{table.Name}", "UNIQUE", value);
            }
            // CurrentKey owns its bytes; retaining it across a batch needs no copy.
            previous = key;
            IndexKeyBuilt?.Invoke(key);
            UniqueIndexEntryValidated?.Invoke();
        }
    }

    // Unpublished trees are never published by unrelated DDL.
    // A crash discards their directory entries; the pending operation rebuilds them.
    // This snapshot may miss a concurrent publication. The catalog merges only
    // this statement's added descriptions under its publication lock.
    private IReadOnlyList<BTreeIndexRegistration> PublishedRegistrations(IReadOnlyList<SqlCatalogIndex> added, bool includePrepared = false)
        => _indexManager.ExportRegistrations(includePrepared).Where(registration =>
            _catalog.TryGetIndex(registration.ObjectId, registration.Definition.Name, out _) ||
            added.Any(index => index.TableObjectId == registration.ObjectId &&
                string.Equals(index.Name, registration.Definition.Name, StringComparison.OrdinalIgnoreCase))).ToArray();
}

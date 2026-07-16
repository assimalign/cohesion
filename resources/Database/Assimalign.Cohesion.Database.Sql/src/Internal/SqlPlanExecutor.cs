using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

using Assimalign.Cohesion.Database.Execution;
using Assimalign.Cohesion.Database.Indexing;
using Assimalign.Cohesion.Database.Sql.Catalog;
using Assimalign.Cohesion.Database.Sql.Language;
using Assimalign.Cohesion.Database.Sql.Storage;
using Assimalign.Cohesion.Database.Storage;
using Assimalign.Cohesion.Database.Transactions;
using Assimalign.Cohesion.Database.Types;

namespace Assimalign.Cohesion.Database.Sql.Internal;

/// <summary>
/// Executes bound plans against shared storage: table scans with predicate
/// filtering, projection/sort/limit for SELECT, typed writes for DML with
/// secondary-index maintenance, and catalog calls for DDL. Results are
/// deterministic: scans yield rows in physical order and ORDER BY sorts are stable.
/// </summary>
internal sealed class SqlPlanExecutor
{
    private readonly SqlStorage _storage;
    private readonly ISqlCatalog _catalog;
    private readonly IIndexManager _indexManager;
    private readonly IReadOnlyDictionary<string, object?>? _parameters;

    internal SqlPlanExecutor(SqlStorage storage, ISqlCatalog catalog, IIndexManager indexManager, IReadOnlyDictionary<string, object?>? parameters)
    {
        _storage = storage;
        _catalog = catalog;
        _indexManager = indexManager;
        _parameters = parameters;
    }

    /// <summary>
    /// A registered index paired with its live tree and resolved key ordinals —
    /// what one statement's maintenance loop works with.
    /// </summary>
    private readonly record struct SqlLiveIndex(SqlCatalogIndex Metadata, IIndex Index, int[] KeyOrdinals);

    internal async Task<QueryResult> ExecuteAsync(SqlPlan plan, SqlStatementContext statement, CancellationToken cancellationToken)
    {
        switch (plan)
        {
            case SqlSelectPlan select:
                return ExecuteSelect(select, statement, cancellationToken);
            case SqlInsertPlan insert:
                return await ExecuteInsertAsync(insert, statement, cancellationToken).ConfigureAwait(false);
            case SqlUpdatePlan update:
                return await ExecuteUpdateAsync(update, statement, cancellationToken).ConfigureAwait(false);
            case SqlDeletePlan delete:
                return await ExecuteDeleteAsync(delete, statement, cancellationToken).ConfigureAwait(false);
            case SqlCreateTablePlan create:
                return await ExecuteCreateTableAsync(create, cancellationToken).ConfigureAwait(false);
            case SqlDropTablePlan drop:
                return await ExecuteDropTableAsync(drop, statement, cancellationToken).ConfigureAwait(false);
            case SqlCreateIndexPlan createIndex:
                return await ExecuteCreateIndexAsync(createIndex, statement, cancellationToken).ConfigureAwait(false);
            case SqlDropIndexPlan dropIndex:
                return await ExecuteDropIndexAsync(dropIndex, statement, cancellationToken).ConfigureAwait(false);
            case SqlAddColumnPlan addColumn:
                await AcquireObjectLockAsync(statement, addColumn.Schema, addColumn.Name, LockMode.Exclusive, cancellationToken).ConfigureAwait(false);
                await _catalog.AddColumnAsync(addColumn.Schema, addColumn.Name, addColumn.Column, cancellationToken).ConfigureAwait(false);
                return new SqlQueryResult(QueryResultStatus.Success, affectedCount: 0);
            case SqlDropColumnPlan dropColumn:
                return await ExecuteDropColumnAsync(dropColumn, statement, cancellationToken).ConfigureAwait(false);
            default:
                throw new DatabaseException($"Plan {plan.GetType().Name} is not executable.");
        }
    }

    // ── SELECT ─────────────────────────────────────────────────────────

    private QueryResult ExecuteSelect(SqlSelectPlan plan, SqlStatementContext statement, CancellationToken cancellationToken)
    {
        var evaluator = new SqlExpressionEvaluator(plan.Table.Columns, _parameters);
        var matches = new List<object?[]>();

        // The access path narrows the candidate set; the full WHERE stays the
        // residual predicate for both paths, so seek results are equivalent to
        // scan results by construction.
        foreach (var (_, values) in EnumerateRows(plan, statement, cancellationToken))
        {
            if (evaluator.Matches(plan.Where, values))
            {
                matches.Add(values);
            }
        }

        if (plan.IsCountStar)
        {
            var countColumns = new[] { new QueryColumn { Name = plan.Projections[0].Name, Ordinal = 0, Type = DatabaseType.Int64 } };
            return new SqlMaterializedResultSet(countColumns, new List<object?[]> { new object?[] { (long)matches.Count } });
        }

        // ORDER BY before projection so sort keys may reference any table column.
        if (plan.OrderBy.Count > 0)
        {
            matches = SortRows(matches, plan.OrderBy, evaluator);
        }

        // Project.
        var projected = new List<object?[]>(matches.Count);
        foreach (var row in matches)
        {
            var output = new object?[plan.Projections.Count];

            for (int i = 0; i < plan.Projections.Count; i++)
            {
                var projection = plan.Projections[i];
                output[i] = projection.ColumnOrdinal is int ordinal
                    ? row[ordinal]
                    : evaluator.Evaluate(projection.Expression!, row);
            }

            projected.Add(output);
        }

        if (plan.IsDistinct)
        {
            projected = Deduplicate(projected);
        }

        // OFFSET / LIMIT.
        IEnumerable<object?[]> window = projected;
        if (plan.Offset is long offset)
        {
            window = window.Skip((int)offset);
        }
        if (plan.Limit is long limit)
        {
            window = window.Take((int)limit);
        }

        var columns = new QueryColumn[plan.Projections.Count];
        for (int i = 0; i < plan.Projections.Count; i++)
        {
            columns[i] = new QueryColumn { Name = plan.Projections[i].Name, Ordinal = i, Type = plan.Projections[i].Type };
        }

        return new SqlMaterializedResultSet(columns, window.ToList());
    }

    private static List<object?[]> SortRows(List<object?[]> rows, IReadOnlyList<SqlOrderByColumn> orderBy, SqlExpressionEvaluator evaluator)
    {
        // Precompute sort keys; OrderBy is a stable sort, satisfying determinism.
        var keyed = rows.Select(row => (Row: row, Keys: orderBy.Select(o => evaluator.Evaluate(o.Expression, row)).ToArray()));

        IOrderedEnumerable<(object?[] Row, object?[] Keys)>? ordered = null;

        for (int i = 0; i < orderBy.Count; i++)
        {
            int index = i;
            var comparer = Comparer<object?>.Create(CompareNullable);

            if (ordered is null)
            {
                ordered = orderBy[index].IsDescending
                    ? keyed.OrderByDescending(x => x.Keys[index], comparer)
                    : keyed.OrderBy(x => x.Keys[index], comparer);
            }
            else
            {
                ordered = orderBy[index].IsDescending
                    ? ordered.ThenByDescending(x => x.Keys[index], comparer)
                    : ordered.ThenBy(x => x.Keys[index], comparer);
            }
        }

        return ordered!.Select(x => x.Row).ToList();

        static int CompareNullable(object? left, object? right) => (left, right) switch
        {
            (null, null) => 0,
            (null, _) => -1, // nulls first
            (_, null) => 1,
            _ => SqlExpressionEvaluator.Compare(left, right),
        };
    }

    private static List<object?[]> Deduplicate(List<object?[]> rows)
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var result = new List<object?[]>(rows.Count);

        foreach (var row in rows)
        {
            // A textual key is sufficient for dedup: values are typed scalars with
            // invariant, prefix-free formatting per field.
            string key = string.Join('\u0001', row.Select(static v =>
                v is null ? "" : $"{v.GetType().Name}:{Convert.ToString(v, CultureInfo.InvariantCulture)}"));

            if (seen.Add(key))
            {
                result.Add(row);
            }
        }

        return result;
    }

    // ── DML ────────────────────────────────────────────────────────────
    //
    // Write statements execute in two phases (area DESIGN §3.8 step 3):
    //
    //   Phase 1 (no physical bracket): scan through the statement snapshot,
    //   collect targets, and acquire locks through the lock manager — an
    //   IntentExclusive lock on the table, then an Exclusive lock per target
    //   row (keyed by the version's packed location, the same identity the
    //   version-store ledger uses). Lock waits are asynchronous, honor the
    //   session's cancellation token, and are where deadlocks are detected
    //   (the requester whose wait would close a cycle aborts).
    //
    //   Phase 2 (the coordinator's gated apply bracket): re-validate each
    //   target against its CURRENT stamps — the latest-state check under the
    //   exclusive lock, the B+Tree uniqueness-discipline precedent; a
    //   snapshot-only check here would admit write skew. A target tombstoned
    //   by a concurrently COMMITTED transaction fails the statement with a
    //   retryable write-write conflict (first-updater-wins); a tombstone from
    //   a transaction that rolled back was already cleared by its undo before
    //   its locks released, so the re-validation passes. Then apply: inserts
    //   and new update-versions stamp the writer, tombstones stamp the
    //   deleter, and every effect is recorded in the version-store ledger so
    //   rollback can undo it logically.

    private async Task<QueryResult> ExecuteInsertAsync(SqlInsertPlan plan, SqlStatementContext statement, CancellationToken cancellationToken)
    {
        var evaluator = new SqlExpressionEvaluator(plan.Table.Columns, _parameters);
        var indexes = GetLiveIndexes(plan.Table);
        var rows = new List<(byte[] Record, object?[] Values)>();

        foreach (var valueRow in plan.Rows)
        {
            var values = new object?[plan.Table.Columns.Count];
            var assigned = new bool[plan.Table.Columns.Count];

            for (int i = 0; i < plan.TargetOrdinals.Count; i++)
            {
                int ordinal = plan.TargetOrdinals[i];
                values[ordinal] = CoerceForColumn(evaluator.Evaluate(valueRow[i], Array.Empty<object?>()), plan.Table.Columns[ordinal]);
                assigned[ordinal] = true;
            }

            for (int ordinal = 0; ordinal < values.Length; ordinal++)
            {
                if (!assigned[ordinal])
                {
                    values[ordinal] = ResolveDefault(plan.Table.Columns[ordinal]);
                }
            }

            rows.Add((SqlRowCodec.Encode(plan.Table.ObjectId, plan.Table.Columns, values, statement.Transaction.Sequence), values));
        }

        // Inserts need no row locks (the rows do not exist yet); the intent
        // lock coordinates with table-grain DDL, and every unique key the
        // statement will touch is locked here — before the apply gate — per the
        // lock-ordering rule.
        await statement.Coordinator.LockManager.AcquireAsync(
            statement.Transaction.Sequence, LockResource.Object(plan.Table.ObjectId), LockMode.IntentExclusive, cancellationToken).ConfigureAwait(false);

        var uniqueKeyHashes = new List<ulong>();
        foreach (var (_, values) in rows)
        {
            CollectUniqueKeyHashes(indexes, plan.Table, values, uniqueKeyHashes);
        }

        await AcquireUniqueKeyLocksAsync(statement, plan.Table.ObjectId, uniqueKeyHashes, cancellationToken).ConfigureAwait(false);

        try
        {
            return await statement.Coordinator.ApplyStatementAsync(statement.Transaction, async bracket =>
            {
                foreach (var (record, values) in rows)
                {
                    var (pageId, slotIndex) = _storage.InsertRow(bracket, plan.Table.ObjectId, record);
                    statement.Coordinator.VersionStore.RecordCreated(statement.Transaction.Sequence, plan.Table.ObjectId, pageId, slotIndex);

                    await InsertIndexEntriesAsync(
                        statement, indexes, plan.Table, values, SqlRecordLocation.Pack(pageId, slotIndex), cancellationToken).ConfigureAwait(false);
                }

                return (QueryResult)new SqlQueryResult(QueryResultStatus.Success, rows.Count);
            }, durable: false, cancellationToken).ConfigureAwait(false);
        }
        catch (IndexUniqueViolationException exception)
        {
            throw TranslateUniqueViolation(plan.Table, exception);
        }
    }

    private async Task<QueryResult> ExecuteUpdateAsync(SqlUpdatePlan plan, SqlStatementContext statement, CancellationToken cancellationToken)
    {
        var evaluator = new SqlExpressionEvaluator(plan.Table.Columns, _parameters);
        var indexes = GetLiveIndexes(plan.Table);
        var targets = new List<(PageId PageId, int SlotIndex, object?[] Values)>();

        foreach (var (location, values) in Scan(plan.Table, statement, cancellationToken))
        {
            if (evaluator.Matches(plan.Where, values))
            {
                targets.Add((location.PageId, location.SlotIndex, values));
            }
        }

        var replacements = new List<(PageId PageId, int SlotIndex, object?[] OldValues, object?[] NewValues, byte[] NewVersion)>(targets.Count);

        foreach (var (pageId, slotIndex, values) in targets)
        {
            var updated = (object?[])values.Clone();

            foreach (var (ordinal, expression) in plan.Assignments)
            {
                // Assignments evaluate against the ORIGINAL row (SQL semantics).
                updated[ordinal] = CoerceForColumn(evaluator.Evaluate(expression, values), plan.Table.Columns[ordinal]);
            }

            replacements.Add((pageId, slotIndex, values, updated,
                SqlRowCodec.Encode(plan.Table.ObjectId, plan.Table.Columns, updated, statement.Transaction.Sequence)));
        }

        await AcquireRowWriteLocksAsync(statement, plan.Table.ObjectId, targets.ConvertAll(t => (t.PageId, t.SlotIndex)), cancellationToken).ConfigureAwait(false);

        // Unique key locks after the row locks (the lock-ordering rule): both the
        // old key (its entry is tombstoned) and the new key (its entry is
        // inserted) of every unique index.
        var uniqueKeyHashes = new List<ulong>();
        foreach (var (_, _, oldValues, newValues, _) in replacements)
        {
            CollectUniqueKeyHashes(indexes, plan.Table, oldValues, uniqueKeyHashes);
            CollectUniqueKeyHashes(indexes, plan.Table, newValues, uniqueKeyHashes);
        }

        await AcquireUniqueKeyLocksAsync(statement, plan.Table.ObjectId, uniqueKeyHashes, cancellationToken).ConfigureAwait(false);

        try
        {
            return await statement.Coordinator.ApplyStatementAsync(statement.Transaction, async bracket =>
            {
                foreach (var (pageId, slotIndex, oldValues, newValues, newVersion) in replacements)
                {
                    EnsureLatestVersion(plan.Table, pageId, slotIndex, statement.Transaction.Sequence);

                    // MVCC update = version chain in the record space: tombstone the
                    // old version in place (same-length write — never relocates) and
                    // insert the new version, both stamped with this transaction's
                    // sequence. The index entries mirror the row versions exactly:
                    // the old location's entries get the same deleter, the new
                    // location gets fresh entries with the same writer.
                    TombstoneVersion(statement, bracket, plan.Table.ObjectId, pageId, slotIndex);
                    await TombstoneIndexEntriesAsync(
                        statement, indexes, plan.Table, oldValues, SqlRecordLocation.Pack(pageId, slotIndex), cancellationToken).ConfigureAwait(false);

                    var location = _storage.InsertRow(bracket, plan.Table.ObjectId, newVersion);
                    statement.Coordinator.VersionStore.RecordCreated(statement.Transaction.Sequence, plan.Table.ObjectId, location.PageId, location.SlotIndex);
                    await InsertIndexEntriesAsync(
                        statement, indexes, plan.Table, newValues, SqlRecordLocation.Pack(location.PageId, location.SlotIndex), cancellationToken).ConfigureAwait(false);
                }

                return (QueryResult)new SqlQueryResult(QueryResultStatus.Success, replacements.Count);
            }, durable: false, cancellationToken).ConfigureAwait(false);
        }
        catch (IndexUniqueViolationException exception)
        {
            throw TranslateUniqueViolation(plan.Table, exception);
        }
    }

    private async Task<QueryResult> ExecuteDeleteAsync(SqlDeletePlan plan, SqlStatementContext statement, CancellationToken cancellationToken)
    {
        var evaluator = new SqlExpressionEvaluator(plan.Table.Columns, _parameters);
        var indexes = GetLiveIndexes(plan.Table);
        var targets = new List<(PageId PageId, int SlotIndex, object?[] Values)>();

        foreach (var (location, values) in Scan(plan.Table, statement, cancellationToken))
        {
            if (evaluator.Matches(plan.Where, values))
            {
                targets.Add((location.PageId, location.SlotIndex, values));
            }
        }

        await AcquireRowWriteLocksAsync(statement, plan.Table.ObjectId, targets.ConvertAll(t => (t.PageId, t.SlotIndex)), cancellationToken).ConfigureAwait(false);

        // Deletes on unique indexes take the key lock too (the B+Tree
        // delete-side discipline) — acquired here, before the apply gate.
        var uniqueKeyHashes = new List<ulong>();
        foreach (var (_, _, values) in targets)
        {
            CollectUniqueKeyHashes(indexes, plan.Table, values, uniqueKeyHashes);
        }

        await AcquireUniqueKeyLocksAsync(statement, plan.Table.ObjectId, uniqueKeyHashes, cancellationToken).ConfigureAwait(false);

        return await statement.Coordinator.ApplyStatementAsync(statement.Transaction, async bracket =>
        {
            foreach (var (pageId, slotIndex, values) in targets)
            {
                EnsureLatestVersion(plan.Table, pageId, slotIndex, statement.Transaction.Sequence);

                // Tombstone, not slot removal: older snapshots must keep seeing
                // the row until the purge worker reclaims versions below every
                // live snapshot's horizon. The index entries mirror the row
                // version's deleter stamp.
                TombstoneVersion(statement, bracket, plan.Table.ObjectId, pageId, slotIndex);
                await TombstoneIndexEntriesAsync(
                    statement, indexes, plan.Table, values, SqlRecordLocation.Pack(pageId, slotIndex), cancellationToken).ConfigureAwait(false);
            }

            return (QueryResult)new SqlQueryResult(QueryResultStatus.Success, targets.Count);
        }, durable: false, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Phase-one lock acquisition for a write statement: IntentExclusive on the
    /// table, then Exclusive per target row in deterministic (location) order.
    /// Locks belong to the transaction and release as a set at commit/rollback
    /// (two-phase locking); deadlocks surface here as the lock manager's
    /// requester-closes-cycle abort.
    /// </summary>
    private static async ValueTask AcquireRowWriteLocksAsync(
        SqlStatementContext statement,
        ulong objectId,
        List<(PageId PageId, int SlotIndex)> targets,
        CancellationToken cancellationToken)
    {
        var locks = statement.Coordinator.LockManager;
        var owner = statement.Transaction.Sequence;

        await locks.AcquireAsync(owner, LockResource.Object(objectId), LockMode.IntentExclusive, cancellationToken).ConfigureAwait(false);

        var keys = targets.ConvertAll(target => SqlRecordLocation.Pack(target.PageId, target.SlotIndex));
        keys.Sort();

        foreach (ulong key in keys)
        {
            await locks.AcquireAsync(owner, LockResource.Entry(objectId, key), LockMode.Exclusive, cancellationToken).ConfigureAwait(false);
        }
    }

    // ── Secondary-index maintenance ────────────────────────────────────
    //
    // Lock-ordering rule (keep it consistent across INSERT/UPDATE/DELETE so
    // cycles stay detectable and rare): a write statement acquires, in phase
    // one and in deterministic order,
    //
    //   1. the table's IntentExclusive object lock,
    //   2. its target rows' Exclusive locks, sorted by packed location,
    //   3. its unique-index key locks, sorted by key hash (deduplicated).
    //
    // The B+Tree re-acquires the key lock inside the apply gate — a same-owner
    // re-grant that completes synchronously — so no lock wait can ever happen
    // while the apply gate is held (a wait there would be invisible to the lock
    // manager's deadlock detection). Non-unique indexes take no key locks at
    // all. Key locks and row locks share the Entry resource space (a hash could
    // collide with a packed location); a collision only over-locks, and the
    // class ordering above keeps acquisition order globally consistent.

    /// <summary>
    /// Resolves a column name to its ordinal (case-insensitive, SQL identifier
    /// rules). Index key columns are validated at plan/DDL time, so a miss here
    /// is a programming error surfaced loudly.
    /// </summary>
    private static int FindColumnOrdinal(SqlCatalogTable table, string columnName)
    {
        for (int i = 0; i < table.Columns.Count; i++)
        {
            if (string.Equals(table.Columns[i].Name, columnName, StringComparison.OrdinalIgnoreCase))
            {
                return i;
            }
        }

        throw new DatabaseException($"Table '{table.Schema}.{table.Name}' has no column named '{columnName}'.");
    }

    /// <summary>
    /// Resolves the table's registered indexes to their live trees and key
    /// ordinals for one statement.
    /// </summary>
    private List<SqlLiveIndex> GetLiveIndexes(SqlCatalogTable table)
    {
        var metadataList = _catalog.GetIndexes(table.ObjectId);

        if (metadataList.Count == 0)
        {
            return new List<SqlLiveIndex>();
        }

        var result = new List<SqlLiveIndex>(metadataList.Count);

        foreach (var metadata in metadataList)
        {
            if (!_indexManager.TryGetIndex(table.ObjectId, metadata.Name, out var index))
            {
                // Defensive: metadata and registrations persist atomically, so a
                // described index always has a tree; tolerate a torn state by
                // skipping rather than failing writes.
                continue;
            }

            var ordinals = new int[metadata.ColumnNames.Count];
            for (int i = 0; i < ordinals.Length; i++)
            {
                ordinals[i] = FindColumnOrdinal(table, metadata.ColumnNames[i]);
            }

            result.Add(new SqlLiveIndex(metadata, index, ordinals));
        }

        return result;
    }

    /// <summary>
    /// Builds an index key from a row's values: one order-preserving component
    /// per key column, encoded exactly like the row payload encodes the value
    /// (null components participate — nulls sort first and count as key values).
    /// </summary>
    private static IndexKey BuildIndexKey(SqlCatalogTable table, int[] keyOrdinals, object?[] values)
    {
        var writer = new DatabaseKeyWriter();

        foreach (int ordinal in keyOrdinals)
        {
            SqlRowCodec.AppendValue(writer, table.Columns[ordinal].Type.Type, values[ordinal]);
        }

        return IndexKey.From(writer);
    }

    /// <summary>
    /// Collects the key-lock hashes a row contributes on every unique index.
    /// </summary>
    private static void CollectUniqueKeyHashes(List<SqlLiveIndex> indexes, SqlCatalogTable table, object?[] values, List<ulong> hashes)
    {
        foreach (var liveIndex in indexes)
        {
            if (liveIndex.Metadata.IsUnique)
            {
                hashes.Add(BuildIndexKey(table, liveIndex.KeyOrdinals, values).Hash());
            }
        }
    }

    /// <summary>
    /// Phase-one acquisition of the statement's unique-index key locks, in
    /// deterministic (sorted, deduplicated) order — after the row locks, per the
    /// lock-ordering rule above.
    /// </summary>
    private static async ValueTask AcquireUniqueKeyLocksAsync(
        SqlStatementContext statement,
        ulong objectId,
        List<ulong> keyHashes,
        CancellationToken cancellationToken)
    {
        if (keyHashes.Count == 0)
        {
            return;
        }

        keyHashes.Sort();

        ulong? previous = null;
        foreach (ulong hash in keyHashes)
        {
            if (previous == hash)
            {
                continue;
            }

            previous = hash;
            await statement.Coordinator.LockManager.AcquireAsync(
                statement.Transaction.Sequence, LockResource.Entry(objectId, hash), LockMode.Exclusive, cancellationToken).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Inserts one row version's entries into every index, stamped with the
    /// writing transaction's sequence, and records them in the version-store
    /// ledger so a logical rollback erases them again.
    /// </summary>
    private async ValueTask InsertIndexEntriesAsync(
        SqlStatementContext statement,
        List<SqlLiveIndex> indexes,
        SqlCatalogTable table,
        object?[] values,
        ulong entryReference,
        CancellationToken cancellationToken)
    {
        foreach (var liveIndex in indexes)
        {
            var key = BuildIndexKey(table, liveIndex.KeyOrdinals, values);
            await liveIndex.Index.InsertAsync(statement.Transaction, key, entryReference, cancellationToken).ConfigureAwait(false);
            statement.Coordinator.VersionStore.RecordIndexEntryCreated(statement.Transaction.Sequence, liveIndex.Index, key, entryReference);
        }
    }

    /// <summary>
    /// Tombstones one row version's entries in every index with the writing
    /// transaction's sequence (mirroring the row version's deleter stamp), and
    /// records them in the ledger so a logical rollback restores them.
    /// </summary>
    private async ValueTask TombstoneIndexEntriesAsync(
        SqlStatementContext statement,
        List<SqlLiveIndex> indexes,
        SqlCatalogTable table,
        object?[] values,
        ulong entryReference,
        CancellationToken cancellationToken)
    {
        foreach (var liveIndex in indexes)
        {
            var key = BuildIndexKey(table, liveIndex.KeyOrdinals, values);
            await liveIndex.Index.DeleteAsync(statement.Transaction, key, entryReference, cancellationToken).ConfigureAwait(false);
            statement.Coordinator.VersionStore.RecordIndexEntryTombstoned(statement.Transaction.Sequence, liveIndex.Index, key, entryReference);
        }
    }

    /// <summary>
    /// Translates the index layer's unique violation into the area's error
    /// surface at the model boundary (the recorded child-root error policy).
    /// The statement's physical bracket has already rolled back — the session
    /// stays usable.
    /// </summary>
    private static DatabaseException TranslateUniqueViolation(SqlCatalogTable table, IndexUniqueViolationException exception)
        => new(
            $"UNIQUE constraint violation on '{table.Schema}.{table.Name}': {exception.Message}",
            exception);

    /// <summary>
    /// The latest-state check under the exclusive row lock: a target tombstoned
    /// by a concurrently committed transaction fails the statement with a
    /// retryable write-write conflict — first-updater-wins. (A snapshot
    /// visibility check alone would admit write skew; this is the B+Tree
    /// uniqueness-discipline precedent applied to row updates.)
    /// </summary>
    /// <exception cref="TransactionAbortedException">The row was modified by a concurrently committed transaction.</exception>
    private void EnsureLatestVersion(SqlCatalogTable table, PageId pageId, int slotIndex, TransactionSequence self)
    {
        var record = _storage.ReadRow(pageId, slotIndex);

        if (record.Length < SqlRowCodec.StampHeaderSize)
        {
            throw new TransactionAbortedException(
                $"Write-write conflict on '{table.Schema}.{table.Name}': the target row version was reclaimed by a concurrent transaction. Retry the transaction.");
        }

        var (_, deleter) = SqlRowCodec.ReadStamps(record.Span);

        if (deleter != TransactionSequence.None && deleter != self)
        {
            throw new TransactionAbortedException(
                $"Write-write conflict on '{table.Schema}.{table.Name}': the row was modified by concurrently committed transaction {deleter} (first-updater-wins). Retry the transaction.");
        }
    }

    /// <summary>
    /// Stamps the record's deleter with the statement's transaction sequence —
    /// the same-length in-place tombstone write — and records it in the
    /// version-store ledger for logical undo and pruning.
    /// </summary>
    private void TombstoneVersion(SqlStatementContext statement, IStorageTransaction bracket, ulong objectId, PageId pageId, int slotIndex)
    {
        var current = _storage.ReadRow(pageId, slotIndex);
        byte[] tombstoned = SqlRowCodec.WithDeleter(current.Span, statement.Transaction.Sequence);
        _storage.UpdateRow(bracket, pageId, slotIndex, tombstoned);
        statement.Coordinator.VersionStore.RecordTombstoned(statement.Transaction.Sequence, objectId, pageId, slotIndex);
    }

    // ── DDL ────────────────────────────────────────────────────────────

    private async Task<QueryResult> ExecuteCreateTableAsync(SqlCreateTablePlan plan, CancellationToken cancellationToken)
    {
        if (plan.IfNotExists && _catalog.TryGetTable(plan.Schema, plan.Name, out _))
        {
            return new SqlQueryResult(QueryResultStatus.Success, affectedCount: 0);
        }

        await _catalog.CreateTableAsync(plan.Schema, plan.Name, plan.Columns, plan.PrimaryKey, cancellationToken).ConfigureAwait(false);
        return new SqlQueryResult(QueryResultStatus.Success, affectedCount: 0);
    }

    /// <summary>
    /// Drops a column and rewrites the table's rows to the new positional layout —
    /// row records are positional, so removing a middle column requires splicing
    /// every stored row (ADD COLUMN, by contrast, is O(1): missing trailing
    /// components decode as null). Runs under the table's Exclusive lock: the
    /// intent-lock matrix makes the rewrite wait for in-flight row writers (and
    /// them for it), so no writer's uncommitted version can be rewritten from
    /// under it.
    /// </summary>
    private async Task<QueryResult> ExecuteDropColumnAsync(SqlDropColumnPlan plan, SqlStatementContext statement, CancellationToken cancellationToken)
    {
        if (!_catalog.TryGetTable(plan.Schema, plan.Name, out var before))
        {
            throw new DatabaseException($"Table '{plan.Schema}.{plan.Name}' does not exist.");
        }

        await statement.Coordinator.LockManager.AcquireAsync(
            statement.Transaction.Sequence, LockResource.Object(before.ObjectId), LockMode.Exclusive, cancellationToken).ConfigureAwait(false);

        int droppedOrdinal = -1;
        for (int i = 0; i < before.Columns.Count; i++)
        {
            if (string.Equals(before.Columns[i].Name, plan.ColumnName, StringComparison.OrdinalIgnoreCase))
            {
                droppedOrdinal = i;
                break;
            }
        }

        // The rewrite walks EVERY stored version — visible or not — because the
        // whole record space must stay decodable on the new positional layout;
        // stamps are preserved so visibility is unchanged by the DDL.
        var targets = new List<(PageId PageId, int SlotIndex, object?[] Values, TransactionSequence Writer, TransactionSequence Deleter)>();
        if (droppedOrdinal >= 0)
        {
            foreach (var (location, values, writer, deleter) in ScanVersions(before, cancellationToken))
            {
                targets.Add((location.PageId, location.SlotIndex, values, writer, deleter));
            }
        }

        var updated = await _catalog.DropColumnAsync(plan.Schema, plan.Name, plan.ColumnName, cancellationToken).ConfigureAwait(false);

        return await statement.Coordinator.ApplyStatementAsync(statement.Transaction, bracket =>
        {
            foreach (var (pageId, slotIndex, values, writer, deleter) in targets)
            {
                var spliced = new object?[values.Length - 1];
                for (int i = 0, j = 0; i < values.Length; i++)
                {
                    if (i != droppedOrdinal)
                    {
                        spliced[j++] = values[i];
                    }
                }

                byte[] record = SqlRowCodec.Encode(updated.ObjectId, updated.Columns, spliced, writer);

                if (deleter != TransactionSequence.None)
                {
                    record = SqlRowCodec.WithDeleter(record, deleter);
                }

                try
                {
                    _storage.UpdateRow(bracket, pageId, slotIndex, record);
                }
                catch (SlottedPageException)
                {
                    _storage.DeleteRow(bracket, pageId, slotIndex);
                    _storage.InsertRow(bracket, updated.ObjectId, record);
                }
            }

            return (QueryResult)new SqlQueryResult(QueryResultStatus.Success, affectedCount: 0);
        }, cancellationToken).ConfigureAwait(false);
    }

    private async Task<QueryResult> ExecuteDropTableAsync(SqlDropTablePlan plan, SqlStatementContext statement, CancellationToken cancellationToken)
    {
        if (!_catalog.TryGetTable(plan.Schema, plan.Name, out var table))
        {
            if (plan.IfExists)
            {
                return new SqlQueryResult(QueryResultStatus.Success, affectedCount: 0);
            }

            await _catalog.DropTableAsync(plan.Schema, plan.Name, cancellationToken).ConfigureAwait(false);
            return new SqlQueryResult(QueryResultStatus.Success, affectedCount: 0);
        }

        // The DDL-vs-writer interlock: an Exclusive table lock waits for every
        // in-flight row writer (IntentExclusive holders) to finish before the
        // table is dropped — and blocks new ones until this transaction ends.
        await statement.Coordinator.LockManager.AcquireAsync(
            statement.Transaction.Sequence, LockResource.Object(table.ObjectId), LockMode.Exclusive, cancellationToken).ConfigureAwait(false);

        // The table's indexes fall with it. The catalog removes their metadata
        // and registrations atomically with the table record; the live trees
        // then leave the manager's directory (their pages await vacuum — see the
        // Indexing DESIGN). Order matters: catalog first (durable), directory
        // second, all under the exclusive lock so no writer maintains a ghost.
        var droppedIndexes = _catalog.GetIndexes(table.ObjectId);

        await _catalog.DropTableAsync(plan.Schema, plan.Name, cancellationToken).ConfigureAwait(false);

        foreach (var metadata in droppedIndexes)
        {
            if (_indexManager.TryGetIndex(table.ObjectId, metadata.Name, out _))
            {
                await _indexManager.DropIndexAsync(statement.Transaction, table.ObjectId, metadata.Name, cancellationToken).ConfigureAwait(false);
            }
        }

        // Release the table's record chain: per-object pages make the drop a
        // page-directory walk instead of a garbage legacy. Rides the statement
        // bracket like every DDL row effect (DROP COLUMN's rewrite precedent) —
        // the catalog entry itself is already self-committed, so the release is
        // not undone by rolling back the enclosing transaction; a crash before
        // the bracket proves out restores the pages as an unreachable, safely
        // leaked chain (the catalog no longer references the object).
        return await statement.Coordinator.ApplyStatementAsync(statement.Transaction, bracket =>
        {
            _storage.FreeOwnerPages(bracket, table.ObjectId);
            return (QueryResult)new SqlQueryResult(QueryResultStatus.Success, affectedCount: 0);
        }, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Acquires a table-grain lock for a DDL statement by schema-qualified name.
    /// </summary>
    private async ValueTask AcquireObjectLockAsync(SqlStatementContext statement, string schema, string name, LockMode mode, CancellationToken cancellationToken)
    {
        if (_catalog.TryGetTable(schema, name, out var table))
        {
            await statement.Coordinator.LockManager.AcquireAsync(
                statement.Transaction.Sequence, LockResource.Object(table.ObjectId), mode, cancellationToken).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Creates a secondary index: a DDL-blocking build under the table's
    /// Exclusive lock (in-flight writers finish first; new ones wait), inside a
    /// gated, <b>durably committed</b> bracket — the self-committing DDL posture,
    /// because the catalog's metadata+registration record commits independently
    /// and must never describe a tree whose pages a crash could revert. The
    /// build walks every stored version — visible or not — and inserts entries
    /// carrying the version's original stamps, so snapshots older than the index
    /// read exactly what the equivalent row scan shows them. Uniqueness is
    /// checked across the live versions during the build (the exclusive lock
    /// excludes concurrent writers, so live state cannot move underneath it).
    /// </summary>
    private async Task<QueryResult> ExecuteCreateIndexAsync(SqlCreateIndexPlan plan, SqlStatementContext statement, CancellationToken cancellationToken)
    {
        if (_catalog.TryGetIndex(plan.Table.ObjectId, plan.IndexName, out _))
        {
            if (plan.IfNotExists)
            {
                return new SqlQueryResult(QueryResultStatus.Success, affectedCount: 0);
            }

            throw new DatabaseException($"An index named '{plan.IndexName}' already exists on '{plan.Table.Schema}.{plan.Table.Name}'.");
        }

        await statement.Coordinator.LockManager.AcquireAsync(
            statement.Transaction.Sequence, LockResource.Object(plan.Table.ObjectId), LockMode.Exclusive, cancellationToken).ConfigureAwait(false);

        var ordinals = new int[plan.ColumnNames.Count];
        for (int i = 0; i < ordinals.Length; i++)
        {
            ordinals[i] = FindColumnOrdinal(plan.Table, plan.ColumnNames[i]);
        }

        var definition = new IndexDefinition(plan.IndexName, IndexKind.BTree, plan.IsUnique);
        bool registered = false;

        try
        {
            await statement.Coordinator.ApplyStatementAsync<bool>(statement.Transaction, async bracket =>
            {
                var index = await _indexManager.CreateIndexAsync(statement.Transaction, plan.Table.ObjectId, definition, cancellationToken).ConfigureAwait(false);
                registered = true;

                var liveKeys = plan.IsUnique ? new HashSet<string>(StringComparer.Ordinal) : null;

                foreach (var (location, values, writer, deleter) in ScanVersions(plan.Table, cancellationToken))
                {
                    var key = BuildIndexKey(plan.Table, ordinals, values);

                    if (liveKeys is not null && deleter == TransactionSequence.None && !liveKeys.Add(Convert.ToHexString(key.Encoded.Span)))
                    {
                        throw new DatabaseException(
                            $"Cannot create UNIQUE index '{plan.IndexName}' on '{plan.Table.Schema}.{plan.Table.Name}': the existing rows contain duplicate keys.");
                    }

                    await index.InsertVersionAsync(
                        bracket, key, SqlRecordLocation.Pack(location.PageId, location.SlotIndex), writer, deleter, cancellationToken).ConfigureAwait(false);
                }

                return true;
            }, durable: true, cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            // The bracket rolled back physically (the tree's pages reverted);
            // unregister the in-memory directory entry it left behind. The
            // catalog was never touched.
            if (registered)
            {
                await _indexManager.DropIndexAsync(statement.Transaction, plan.Table.ObjectId, plan.IndexName, CancellationToken.None).ConfigureAwait(false);
            }

            throw;
        }

        // Metadata + registrations persist in ONE catalog self-commit: a crash
        // cannot leave a described index without a tree registration (or the
        // reverse). A crash before this write leaves only orphaned tree pages —
        // a safe leak, never a re-attached index.
        var registrations = ((IIndexRegistry)_indexManager).ExportRegistrations();
        await _catalog.CreateIndexAsync(
            new SqlCatalogIndex(plan.Table.ObjectId, plan.IndexName, plan.ColumnNames, plan.IsUnique),
            registrations,
            cancellationToken).ConfigureAwait(false);

        return new SqlQueryResult(QueryResultStatus.Success, affectedCount: 0);
    }

    /// <summary>
    /// Drops a secondary index under the table's Exclusive lock: the catalog
    /// removes the metadata and registration first (one durable self-commit —
    /// the authoritative drop), then the live tree leaves the manager's
    /// directory; its pages await vacuum. The exclusive lock excludes writers
    /// for the whole statement, so no maintenance can race the two steps.
    /// </summary>
    private async Task<QueryResult> ExecuteDropIndexAsync(SqlDropIndexPlan plan, SqlStatementContext statement, CancellationToken cancellationToken)
    {
        if (!_catalog.TryGetIndex(plan.Table.ObjectId, plan.IndexName, out var metadata))
        {
            if (plan.IfExists)
            {
                return new SqlQueryResult(QueryResultStatus.Success, affectedCount: 0);
            }

            throw new DatabaseException($"No index named '{plan.IndexName}' exists on '{plan.Table.Schema}.{plan.Table.Name}'.");
        }

        await statement.Coordinator.LockManager.AcquireAsync(
            statement.Transaction.Sequence, LockResource.Object(plan.Table.ObjectId), LockMode.Exclusive, cancellationToken).ConfigureAwait(false);

        // The canonical (creation-time) name keyed by the catalog drives the
        // manager lookup: catalog names are case-insensitive, directory names
        // are exact.
        var remaining = new List<BTreeIndexRegistration>();
        foreach (var registration in ((IIndexRegistry)_indexManager).ExportRegistrations())
        {
            if (!(registration.ObjectId == plan.Table.ObjectId &&
                  string.Equals(registration.Definition.Name, metadata.Name, StringComparison.Ordinal)))
            {
                remaining.Add(registration);
            }
        }

        await _catalog.DropIndexAsync(plan.Table.ObjectId, metadata.Name, remaining, cancellationToken).ConfigureAwait(false);

        if (_indexManager.TryGetIndex(plan.Table.ObjectId, metadata.Name, out _))
        {
            await _indexManager.DropIndexAsync(statement.Transaction, plan.Table.ObjectId, metadata.Name, cancellationToken).ConfigureAwait(false);
        }

        return new SqlQueryResult(QueryResultStatus.Success, affectedCount: 0);
    }

    // ── Scan / seek + coercion helpers ─────────────────────────────────

    /// <summary>
    /// Enumerates the SELECT's candidate rows through its access path: an index
    /// seek drives a B+Tree cursor with the statement snapshot; everything else
    /// is the per-object scan. A seek whose index is not attached (torn-state
    /// defense) falls back to the scan — the residual predicate makes the
    /// fallback invisible except in cost.
    /// </summary>
    private IEnumerable<((PageId PageId, int SlotIndex) Location, object?[] Values)> EnumerateRows(
        SqlSelectPlan plan,
        SqlStatementContext statement,
        CancellationToken cancellationToken)
    {
        if (plan.Access is SqlIndexSeekPath seek &&
            _indexManager.TryGetIndex(plan.Table.ObjectId, seek.Index.Name, out var index))
        {
            statement.Metrics.AccessPath = $"seek:{seek.Index.Name}";
            return SeekRows(plan.Table, seek, index, statement, cancellationToken);
        }

        statement.Metrics.AccessPath = "scan";
        return Scan(plan.Table, statement, cancellationToken);
    }

    /// <summary>
    /// Drives an index seek: materializes the visible entries in the seek's key
    /// range through the STATEMENT snapshot (the same snapshot the equivalent
    /// scan filters through — the equivalence anchor), fetches each entry's row
    /// version, and re-checks the row's stamps against the same snapshot
    /// (defense in depth: entries mirror row stamps by the maintenance
    /// discipline, so a divergence is a bug this filter contains rather than
    /// surfaces).
    /// </summary>
    private IEnumerable<((PageId PageId, int SlotIndex) Location, object?[] Values)> SeekRows(
        SqlCatalogTable table,
        SqlIndexSeekPath seek,
        IIndex index,
        SqlStatementContext statement,
        CancellationToken cancellationToken)
    {
        var range = BuildSeekRange(table, seek);
        var snapshot = statement.Snapshot;

        // The cursor materializes under the tree's read latch; synchronous
        // drain is the in-process fast path.
        var cursor = index.OpenCursor(snapshot, range);

        try
        {
            while (cursor.MoveNextAsync(cancellationToken).AsTask().GetAwaiter().GetResult())
            {
                statement.Metrics.RecordsExamined++;

                var (pageId, slotIndex) = SqlRecordLocation.Unpack(cursor.CurrentEntryReference);

                ReadOnlyMemory<byte> record;
                try
                {
                    record = _storage.ReadRow(pageId, slotIndex);
                }
                catch (StorageException)
                {
                    continue; // reclaimed beneath an invisible entry: skip
                }
                catch (ArgumentOutOfRangeException)
                {
                    continue;
                }

                var values = SqlRowCodec.TryDecode(record.Span, table.ObjectId, table.Columns.Count, out var writer, out var deleter);

                if (values is null)
                {
                    continue;
                }

                if (!snapshot.IsVisible(writer))
                {
                    continue;
                }

                if (deleter != TransactionSequence.None && snapshot.IsVisible(deleter))
                {
                    continue;
                }

                yield return ((pageId, slotIndex), values);
            }
        }
        finally
        {
            cursor.DisposeAsync().AsTask().GetAwaiter().GetResult();
        }
    }

    /// <summary>
    /// Encodes the seek's key range: the equality prefix (encoded exactly like
    /// the maintenance path encodes keys), extended by the optional range bounds
    /// on the next key column. Prefix semantics ride the codec's
    /// order-preservation: every composite key starting with prefix P sorts in
    /// [P, successor(P)), where successor increments the last non-0xFF byte.
    /// </summary>
    private static IndexKeyRange BuildSeekRange(SqlCatalogTable table, SqlIndexSeekPath seek)
    {
        var prefixWriter = new DatabaseKeyWriter();

        for (int i = 0; i < seek.EqualityValues.Count; i++)
        {
            int ordinal = FindColumnOrdinal(table, seek.Index.ColumnNames[i]);
            SqlRowCodec.AppendValue(prefixWriter, table.Columns[ordinal].Type.Type, seek.EqualityValues[i]);
        }

        byte[] prefix = prefixWriter.ToArray();

        if (seek.Lower is null && seek.Upper is null)
        {
            return new IndexKeyRange(
                new IndexKey(prefix),
                PrefixSuccessor(prefix) is { } successor ? new IndexKey(successor) : null,
                IsStartInclusive: true,
                IsEndInclusive: false);
        }

        int rangeOrdinal = FindColumnOrdinal(table, seek.Index.ColumnNames[seek.EqualityValues.Count]);
        var rangeType = table.Columns[rangeOrdinal].Type.Type;

        IndexKey? start = seek.EqualityValues.Count > 0 ? new IndexKey(prefix) : null;
        bool startInclusive = true;
        IndexKey? end = seek.EqualityValues.Count > 0 && PrefixSuccessor(prefix) is { } prefixEnd ? new IndexKey(prefixEnd) : null;
        bool endInclusive = false;

        if (seek.Lower is { } lower)
        {
            byte[] lowerKey = AppendComponent(prefix, rangeType, lower.Value);

            // Exclusive lower: skip every composite key whose range component
            // equals the bound — start at the bound's prefix successor.
            start = lower.Inclusive
                ? new IndexKey(lowerKey)
                : PrefixSuccessor(lowerKey) is { } lowerSuccessor ? new IndexKey(lowerSuccessor) : null;
            startInclusive = true;
        }

        if (seek.Upper is { } upper)
        {
            byte[] upperKey = AppendComponent(prefix, rangeType, upper.Value);

            // Inclusive upper: admit every composite key whose range component
            // equals the bound — end (exclusively) at the bound's successor.
            end = upper.Inclusive
                ? PrefixSuccessor(upperKey) is { } upperSuccessor ? new IndexKey(upperSuccessor) : null
                : new IndexKey(upperKey);
            endInclusive = false;
        }

        return new IndexKeyRange(start, end, startInclusive, endInclusive);
    }

    private static byte[] AppendComponent(byte[] prefix, DatabaseType type, object? value)
    {
        var writer = new DatabaseKeyWriter();
        SqlRowCodec.AppendValue(writer, type, value);
        byte[] component = writer.ToArray();

        var combined = new byte[prefix.Length + component.Length];
        prefix.CopyTo(combined, 0);
        component.CopyTo(combined, prefix.Length);
        return combined;
    }

    /// <summary>
    /// Computes the smallest byte string greater than every string with the
    /// given prefix, or null when none exists (an all-0xFF prefix): increment
    /// the last non-0xFF byte and truncate behind it.
    /// </summary>
    private static byte[]? PrefixSuccessor(byte[] prefix)
    {
        for (int i = prefix.Length - 1; i >= 0; i--)
        {
            if (prefix[i] != 0xFF)
            {
                var successor = new byte[i + 1];
                prefix.AsSpan(0, i + 1).CopyTo(successor);
                successor[i]++;
                return successor;
            }
        }

        return null;
    }

    /// <summary>
    /// Scans the table's visible row versions through the statement's snapshot:
    /// a version is visible when its writer is admitted and its deleter (when
    /// stamped) is not — a visible tombstone reads as absence. Exactly one
    /// version of a logical row is visible per snapshot by construction: an
    /// update stamps the old version's deleter with the same sequence that wrote
    /// the new version.
    /// </summary>
    private IEnumerable<((PageId PageId, int SlotIndex) Location, object?[] Values)> Scan(
        SqlCatalogTable table,
        SqlStatementContext statement,
        CancellationToken cancellationToken)
    {
        var snapshot = statement.Snapshot;

        foreach (var version in ScanVersions(table, cancellationToken, statement.Metrics))
        {
            if (!snapshot.IsVisible(version.Writer))
            {
                continue;
            }

            if (version.Deleter != TransactionSequence.None && snapshot.IsVisible(version.Deleter))
            {
                continue; // a visible tombstone reads as absence
            }

            yield return (version.Location, version.Values);
        }
    }

    /// <summary>
    /// Scans every stored version of the table's rows — visible or not — with
    /// its stamps. DDL row rewrites use this: the whole record space must stay
    /// decodable across a layout change, so tombstoned and concurrent versions
    /// rewrite too, stamps preserved. The scan is scoped to the table's record
    /// chain (per-object pages), so its cost is O(table), not O(database); the
    /// object-id prefix filter below stays as defense in depth.
    /// </summary>
    private IEnumerable<((PageId PageId, int SlotIndex) Location, object?[] Values, TransactionSequence Writer, TransactionSequence Deleter)> ScanVersions(
        SqlCatalogTable table,
        CancellationToken cancellationToken,
        SqlStatementMetrics? metrics = null)
    {
        using var iterator = _storage.GetUnitIterator(table.ObjectId);

        while (iterator.MoveNext())
        {
            cancellationToken.ThrowIfCancellationRequested();

            var unit = iterator.Current;
            var values = SqlRowCodec.TryDecode(unit.Data.Span, table.ObjectId, table.Columns.Count, out var writer, out var deleter);

            if (values is not null)
            {
                if (metrics is not null)
                {
                    metrics.RecordsExamined++;
                }

                yield return ((unit.PageId, unit.SlotIndex), values, writer, deleter);
            }
        }
    }

    /// <summary>
    /// Coerces an evaluated value to a column's storage type, enforcing nullability.
    /// </summary>
    internal static object? CoerceForColumn(object? value, SqlCatalogColumn column)
    {
        if (value is null)
        {
            if (!column.IsNullable)
            {
                throw new DatabaseException($"Column '{column.Name}' does not allow NULL.");
            }

            return null;
        }

        try
        {
            return column.Type.Type switch
            {
                DatabaseType.Boolean => Convert.ToBoolean(value, CultureInfo.InvariantCulture),
                DatabaseType.Int8 => Convert.ToSByte(value, CultureInfo.InvariantCulture),
                DatabaseType.Int16 => Convert.ToInt16(value, CultureInfo.InvariantCulture),
                DatabaseType.Int32 => Convert.ToInt32(value, CultureInfo.InvariantCulture),
                DatabaseType.Int64 => Convert.ToInt64(value, CultureInfo.InvariantCulture),
                DatabaseType.Float32 => Convert.ToSingle(value, CultureInfo.InvariantCulture),
                DatabaseType.Float64 => Convert.ToDouble(value, CultureInfo.InvariantCulture),
                DatabaseType.Decimal => Convert.ToDecimal(value, CultureInfo.InvariantCulture),
                DatabaseType.String or DatabaseType.Json => value as string
                    ?? Convert.ToString(value, CultureInfo.InvariantCulture)!,
                DatabaseType.Binary or DatabaseType.JsonBinary => value is byte[] bytes
                    ? bytes
                    : throw new DatabaseException($"Column '{column.Name}' requires binary data."),
                DatabaseType.Date => value switch
                {
                    DateOnly date => date,
                    string text => DateOnly.Parse(text, CultureInfo.InvariantCulture),
                    DateTime dateTime => DateOnly.FromDateTime(dateTime),
                    _ => throw new DatabaseException($"Cannot convert {value.GetType().Name} to DATE."),
                },
                DatabaseType.Time => value switch
                {
                    TimeOnly time => time,
                    string text => TimeOnly.Parse(text, CultureInfo.InvariantCulture),
                    _ => throw new DatabaseException($"Cannot convert {value.GetType().Name} to TIME."),
                },
                DatabaseType.DateTime => value switch
                {
                    DateTime dateTime => dateTime,
                    string text => DateTime.Parse(text, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind),
                    _ => throw new DatabaseException($"Cannot convert {value.GetType().Name} to TIMESTAMP."),
                },
                DatabaseType.DateTimeOffset => value switch
                {
                    DateTimeOffset dateTimeOffset => dateTimeOffset,
                    string text => DateTimeOffset.Parse(text, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind),
                    _ => throw new DatabaseException($"Cannot convert {value.GetType().Name} to TIMESTAMPTZ."),
                },
                DatabaseType.TimeSpan => value switch
                {
                    TimeSpan timeSpan => timeSpan,
                    string text => TimeSpan.Parse(text, CultureInfo.InvariantCulture),
                    _ => throw new DatabaseException($"Cannot convert {value.GetType().Name} to INTERVAL."),
                },
                DatabaseType.Guid => value switch
                {
                    Guid guid => guid,
                    string text => Guid.Parse(text),
                    _ => throw new DatabaseException($"Cannot convert {value.GetType().Name} to UUID."),
                },
                _ => throw new DatabaseException($"Column type {column.Type.Type} cannot store values yet."),
            };
        }
        catch (Exception exception) when (exception is FormatException or OverflowException or InvalidCastException)
        {
            throw new DatabaseException(
                $"Cannot convert value '{value}' to column '{column.Name}' of type {column.Type.Type}.", exception);
        }
    }

    private static object? ResolveDefault(SqlCatalogColumn column)
    {
        if (column.DefaultLiteral is null)
        {
            if (!column.IsNullable)
            {
                throw new DatabaseException($"Column '{column.Name}' does not allow NULL and has no default.");
            }

            return null;
        }

        return CoerceForColumn(column.DefaultLiteral, column);
    }
}

using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

using Assimalign.Cohesion.Database.Sql.Catalog;
using Assimalign.Cohesion.Database.Storage;
using Assimalign.Cohesion.Database.Transactions;
using Assimalign.Cohesion.Database.Types;

namespace Assimalign.Cohesion.Database.Sql.Internal;

internal sealed partial class SqlPlanExecutor
{
    // UPDATE and ON CONFLICT share one final-state view, including incoming references
    // whose old parent key may be provided by a different replacement in a key swap.
    private static SqlConstraintUpdates BuildConstraintUpdates(SqlCatalogTable table,
        IEnumerable<(ulong Location, object?[] OldValues, object?[] NewValues)> replacements,
        IReadOnlyList<(int Ordinal, SqlBoundExpression Value)> assignments)
    {
        var rows = new Dictionary<(ulong ObjectId, ulong Location), SqlConstraintUpdate>();
        var assigned = assignments.Select(assignment => assignment.Ordinal).ToHashSet();
        foreach (var row in replacements)
        {
            rows.Add((table.ObjectId, row.Location), new SqlConstraintUpdate(table,
                SqlRecordLocation.Unpack(row.Location), row.OldValues, row.NewValues, assigned));
        }
        return new SqlConstraintUpdates(rows);
    }

    // Constraint validation sees the statement's final row set. Lookups still use the
    // existing indexes and snapshot; pending replacements contribute their final keys.
    private IEnumerable<((PageId PageId, int SlotIndex) Location, object?[] Values)> FinalConstraintRows(
        SqlBoundConstraintKey projection, object?[] keys, SqlStatementContext statement,
        CancellationToken cancellationToken,
        Dictionary<(ulong ObjectId, ulong Location), SqlConstraintDelete>? deletions,
        SqlConstraintUpdates? updates, TransactionSnapshot? snapshot = null)
    {
        var table = projection.Table;
        foreach (var row in FindConstraintRows(projection, keys, statement, cancellationToken, snapshot))
        {
            var key = (table.ObjectId, SqlRecordLocation.Pack(row.Location.PageId, row.Location.SlotIndex));
            if (deletions?.ContainsKey(key) != true && updates?.ContainsKey(key) != true) { yield return row; }
        }
        if (updates is null) { yield break; }
        foreach (var row in updates.Find(projection, keys, statement.Metrics, cancellationToken))
        {
            var key = (table.ObjectId, SqlRecordLocation.Pack(row.Location.PageId, row.Location.SlotIndex));
            if (deletions?.ContainsKey(key) != true) { yield return row; }
        }
    }

    // Built after SET assignments are collected. Stable table/key projections are indexed
    // once, using constraint equality; MERGE invalidates them after compositional changes.
    [global::System.Diagnostics.DebuggerDisplay("SqlConstraintUpdates")]
    [global::System.Diagnostics.DebuggerTypeProxy(typeof(global::Assimalign.Cohesion.Database.Sql.Internal.SqlPlanExecutor.SqlConstraintUpdates.DebugView))]
    private class SqlConstraintUpdates
    {
        private readonly Dictionary<(ulong ObjectId, ulong Location), SqlConstraintUpdate> _rows;
        private Dictionary<ulong, SqlConstraintUpdate[]> _tables;
        private readonly Dictionary<SqlBoundConstraintKey, Dictionary<object?[], List<((PageId PageId, int SlotIndex) Location, object?[] Values)>>> _indexes = [];

        internal SqlConstraintUpdates(Dictionary<(ulong ObjectId, ulong Location), SqlConstraintUpdate> rows)
        {
            _rows = rows;
            _tables = rows.Values.GroupBy(row => row.Table.ObjectId).ToDictionary(group => group.Key, group => group.ToArray());
        }

        internal void Refresh()
        {
            _tables = _rows.Values.GroupBy(row => row.Table.ObjectId).ToDictionary(group => group.Key, group => group.ToArray());
            _indexes.Clear();
            ChargeDirectory();
        }

        internal void ChargeDirectory()
        {
            if (this is SqlMergeConstraintUpdates merge)
            { merge.Memory.Reserve(128L + 32L * _rows.Count + 128L * _tables.Count); }
        }

        internal bool TryGet((ulong ObjectId, ulong Location) key, out SqlConstraintUpdate row) => _rows.TryGetValue(key, out row!);
        internal IEnumerable<SqlConstraintUpdate> Values => _rows.Values;
        internal bool ContainsKey((ulong ObjectId, ulong Location) key) => _rows.ContainsKey(key);

        internal IEnumerable<((PageId PageId, int SlotIndex) Location, object?[] Values)> Find(
            SqlBoundConstraintKey projection, object?[] keys, SqlStatementMetrics metrics, CancellationToken cancellationToken)
        {
            if (!_tables.TryGetValue(projection.Table.ObjectId, out var rows)) { return []; }
            if (!_indexes.TryGetValue(projection, out var index))
            {
                index = new(new GroupKeyComparer(projection.Collations, metrics));
                foreach (var row in rows)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    object?[] key = projection.Read(row.NewValues);
                    if (this is SqlMergeConstraintUpdates merge)
                    {
                        merge.Memory.Retain(key);
                        merge.Memory.Reserve(192); // projected key directory, tuple list and growth
                    }
                    if (!index.TryGetValue(key, out var matches)) { index.Add(key, matches = []); }
                    matches.Add((row.Location, row.NewValues));
                }
                _indexes.Add(projection, index);
            }
            return index.TryGetValue(keys, out var found) ? found : [];
        }

        private sealed class DebugView
        {
            public DebugView(global::Assimalign.Cohesion.Database.Sql.Internal.SqlPlanExecutor.SqlConstraintUpdates value)
            {
                _ = value;
            }
        }
    }

    // Keep MERGE-only memory state out of ordinary constraint objects and their exact
    // Release allocation budgets. The base constructor retains its original allocation.
    private sealed class SqlMergeConstraintUpdates : SqlConstraintUpdates
    {
        internal SqlMergeConstraintUpdates(Dictionary<(ulong ObjectId, ulong Location), SqlConstraintUpdate> rows, SqlDmlMemory memory)
            : base(rows)
        {
            Memory = memory;
            ChargeDirectory();
        }

        internal SqlDmlMemory Memory { get; }
    }

    private async Task<SqlConstraintUpdates> CollectReferentialUpdatesAsync(
        Dictionary<(ulong ObjectId, ulong Location), SqlConstraintDelete> deletions,
        HashSet<ulong> scannedTables, SqlStatementContext statement, CancellationToken cancellationToken,
        Dictionary<(ulong ObjectId, ulong Location), SqlConstraintUpdate>? finalRows = null)
    {
        var updates = finalRows ?? new Dictionary<(ulong ObjectId, ulong Location), SqlConstraintUpdate>();
        var final = finalRows is null ? null : new SqlMergeConstraintUpdates(updates, statement.Metrics.DmlMemory(_definitions.StatementMemoryBudget)!);
        foreach (var deletion in deletions.Values)
        {
            foreach (var reference in IncomingReferences(deletion.Table, statement, cancellationToken))
            {
                var (child, constraint) = reference;
                cancellationToken.ThrowIfCancellationRequested();
                if (constraint.OnDelete is not (SqlCatalogReferentialAction.SetNull or SqlCatalogReferentialAction.SetDefault)) { continue; }
                var keys = reference.ParentKey.Read(deletion.EffectiveValues ?? deletion.Values);
                if (keys.Any(value => value is null)) { continue; }
                var matches = CollectConstraintMatches(final is null
                    ? FindConstraintRows(reference.ChildKey, keys, statement, cancellationToken, ConstraintCurrentSnapshot(statement))
                    : FinalConstraintRows(reference.ChildKey, keys, statement, cancellationToken, deletions, final, ConstraintCurrentSnapshot(statement)), statement);
                var bound = _definitions.Get(child, cancellationToken);
                foreach (var match in matches)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    var key = (child.ObjectId, SqlRecordLocation.Pack(match.Location.PageId, match.Location.SlotIndex));
                    if (deletions.ContainsKey(key)) { continue; }
                    if (!updates.TryGetValue(key, out var update))
                    {
                        await AcquireRowWriteLocksAsync(statement, child.ObjectId, [match.Location], cancellationToken, cascade: true).ConfigureAwait(false);
                        EnsureSnapshotSeesCascadeRow(child, match.Location, statement,
                            constraint.OnDelete == SqlCatalogReferentialAction.SetNull ? "SET NULL" : "SET DEFAULT");
                        if (scannedTables.Add(child.ObjectId))
                        {
                            EnsureCurrentDefinition(child);
                            await AcquireReferenceIntentLocksAsync(child, statement, cancellationToken, cascade: true).ConfigureAwait(false);
                        }
                        update = new SqlConstraintUpdate(child, match.Location, match.Values, (object?[])match.Values.Clone(), []);
                        if (statement.Metrics.DmlMemory(_definitions.StatementMemoryBudget) is { } memory)
                        {
                            memory.Retain(match.Values);
                            memory.Retain(update.NewValues);
                            memory.Reserve(384L + 32L * child.Columns.Count);
                        }
                        updates.Add(key, update);
                    }
                    foreach (int ordinal in reference.ChildKey.Ordinals)
                    {
                        string name = child.Columns[ordinal].Name;
                        if (constraint.OnDelete == SqlCatalogReferentialAction.SetDefault && child.Columns[ordinal].Identity is { IsAlways: true })
                        {
                            throw SqlPlanningException.IdentityViolation(name, ErrorMessages.IdentityUpdateForbidden);
                        }
                        object? value = constraint.OnDelete == SqlCatalogReferentialAction.SetNull ? null
                            : ResolveColumnDefault(child, ordinal, bound.DefaultValues[ordinal], statement, cancellationToken);
                        if (value is null && !child.Columns[ordinal].IsNullable) { throw Violation(child, constraint, keys); }
                        update.NewValues[ordinal] = CoerceForColumn(value, child.Columns[ordinal]);
                        update.Assigned.Add(ordinal);
                    }
                    statement.Metrics.DmlMemory(_definitions.StatementMemoryBudget)?.Retain(update.NewValues);
                }
                final?.Refresh();
            }
        }

        SqlConstraintUpdates indexed = finalRows is null ? new SqlConstraintUpdates(updates)
            : new SqlMergeConstraintUpdates(updates, statement.Metrics.DmlMemory(_definitions.StatementMemoryBudget)!);
        // MERGE validates and locks only after direct and referential actions compose.
        if (finalRows is not null) { return indexed; }
        var references = new List<SqlParentReference>();
        var released = new List<SqlParentReference>();
        foreach (var group in updates.Values.GroupBy(update => update.Table))
        {
            var table = group.Key;
            var bound = _definitions.Get(table, cancellationToken);
            foreach (var update in group)
            {
                cancellationToken.ThrowIfCancellationRequested();
                CheckDomains(bound, update.NewValues, ExecutionEvaluator(null, cancellationToken, transactionTimestamp: statement.TransactionStart), update.Assigned.ToArray());
                ValidateParentUpdate(table, update.OldValues, update.NewValues, statement, cancellationToken, deletions, indexed);
                update.NewVersion = SqlRowCodec.Encode(table, update.NewValues, statement.Transaction.Sequence);
                statement.Metrics.DmlMemory(_definitions.StatementMemoryBudget)?.Retain(update.NewVersion);
            }
            // PostgreSQL ri_triggers.c:2800-2813 uses current parent visibility for ReadCommitted
            // RI writes while transaction snapshots retain their crosscheck.
            ValidateRows(table, group.Select(update => update.NewValues).ToList(), statement, cancellationToken,
                current: statement.Transaction.IsolationLevel == IsolationLevel.ReadCommitted,
                references: references, deletions: deletions, updates: indexed);
            CollectReleasedReferences(table, group.Select(update => update.OldValues).ToList(), released, statement, cancellationToken);
        }
        await AcquireParentRowLocksAsync(references, statement, cancellationToken, cascade: true).ConfigureAwait(false);
        await AcquireParentRowLocksAsync(released, statement, cancellationToken, validate: false, cascade: true).ConfigureAwait(false);
        // All row and reference locks precede unique-key locks and the physical bracket.
        foreach (var group in updates.Values.GroupBy(update => update.Table).OrderBy(group => group.Key.ObjectId))
        {
            var indexes = GetLiveIndexes(group.Key, statement);
            var hashes = new List<ulong>();
            foreach (var update in group)
            {
                cancellationToken.ThrowIfCancellationRequested();
                CollectUniqueKeyHashes(indexes, group.Key, update.OldValues, hashes);
                ValidateIndexKeysAndCollectUniqueHashes(indexes, group.Key, update.NewValues, hashes);
            }
            await AcquireUniqueKeyLocksAsync(statement, group.Key.ObjectId, hashes, cancellationToken, cascade: true).ConfigureAwait(false);
        }
        return indexed;
    }
}

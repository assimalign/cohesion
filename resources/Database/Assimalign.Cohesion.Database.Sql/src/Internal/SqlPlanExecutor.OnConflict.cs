using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

using Assimalign.Cohesion.Database.Execution;
using Assimalign.Cohesion.Database.Indexing;
using Assimalign.Cohesion.Database.Sql.Catalog;
using Assimalign.Cohesion.Database.Sql.Storage;
using Assimalign.Cohesion.Database.Storage;
using Assimalign.Cohesion.Database.Transactions;

namespace Assimalign.Cohesion.Database.Sql.Internal;

internal sealed partial class SqlPlanExecutor
{
    private static readonly string _upsertSnapshotConflict = ErrorMessages.UpsertSnapshotConflict;
    private static readonly string _upsertIndexesChanged = ErrorMessages.UpsertIndexesChanged;
    private sealed record SqlUpsertWrite(ulong? OldLocation, object?[]? OldValues, object?[] Values, byte[] Record);

    private static void EnsureUpsertIndexes(SqlCatalogTable table, List<SqlLiveIndex> indexes, SqlOnConflictPlan? plan)
    {
        if (plan is null) { return; }
        // Index-only DDL keeps the table definition object. Check the bound unique directory
        // after acquiring IX so dropped/replaced arbiters cannot become a plain insert.
        if (indexes.Count(index => index.Metadata.IsUnique) != plan.UniqueIndexes.Count ||
            plan.UniqueIndexes.Any(expected => !indexes.Any(current => ReferenceEquals(current.Metadata, expected))))
        {
            throw new TransactionAbortedException(string.Format(CultureInfo.CurrentCulture,
                _upsertIndexesChanged, table.Schema, table.Name));
        }
    }

    private async Task<DatabaseResult> ExecuteUpsertRowsAsync(SqlCatalogTable table, List<SqlLiveIndex> indexes,
        List<(byte[] Record, object?[] Values)> proposals, SqlOnConflictPlan plan, IReadOnlyList<SqlProjection>? returning,
        SqlStatementContext statement, CancellationToken cancellationToken)
    {
        // PostgreSQL validates proposed NOT NULL/CHECK before arbitration (nodeModifyTable.c:1108).
        // FK checks see only rows actually written, so a discarded proposal cannot authorize a self reference.
        ValidateRows(table, proposals.Select(row => row.Values).ToList(), statement, cancellationToken, checksOnly: true);
        var arbiters = indexes.Where(index => plan.Arbiters.Any(arbiter => arbiter.Name == index.Metadata.Name)).ToList();
        var hashes = new List<ulong>();
        foreach (var proposal in proposals) { CollectUniqueKeyHashes(arbiters, table, proposal.Values, hashes); }
        // Second exception to row-before-key: the complete statement's arbiter keys precede every row lock.
        await AcquireUniqueKeyLocksAsync(statement, table.ObjectId, hashes, cancellationToken).ConfigureAwait(false);

        var pending = new Dictionary<(string Index, IndexKey Key), SqlUpsertWrite?>();
        var affected = new HashSet<ulong>();
        var writes = new List<SqlUpsertWrite>();
        var lockedRows = new List<(PageId PageId, int SlotIndex)>();
        var evaluator = ExecutionEvaluator(_subqueryValues, cancellationToken);
        var bound = _definitions.Get(table, cancellationToken);
        int[]? assigned = bound.Domains.Count == 0 ? null : plan.Assignments.Select(assignment => assignment.Ordinal).ToArray();
        foreach (var proposal in proposals)
        {
            cancellationToken.ThrowIfCancellationRequested();
            ulong? conflict = null;
            bool pendingConflict = false;
            foreach (var arbiter in arbiters)
            {
                var key = BuildIndexKey(table, arbiter.KeyOrdinals, proposal.Values);
                if (pending.TryGetValue((arbiter.Metadata.Name, key), out var prior))
                {
                    if (prior is null) { continue; } // An earlier update released this key.
                    pendingConflict = true;
                    break;
                }
                if (arbiter.Index.FindLiveEntry(key) is { } location) { conflict = location; break; }
            }

            SqlUpsertWrite write;
            if (pendingConflict)
            {
                if (!plan.DoUpdate) { continue; }
                throw new SqlPlanningException("COHSQLE041", ErrorMessages.UpsertRepeatedRow);
            }
            if (conflict is { } reference)
            {
                var (pageId, slotIndex) = SqlRecordLocation.Unpack(reference);
                if (!_storage.TryReadRecord(pageId, slotIndex, table.ObjectId, out var record)) { throw SnapshotConflict(); }
                var oldValues = DecodeRow(record, pageId, slotIndex, table, bound.MissingValues, out var writer, out _);
                if (oldValues is null || statement.Transaction.IsolationLevel != IsolationLevel.ReadCommitted && !statement.Snapshot.IsVisible(writer))
                { throw SnapshotConflict(); }
                if (!plan.DoUpdate) { continue; }
                if (affected.Contains(reference)) { throw new SqlPlanningException("COHSQLE041", ErrorMessages.UpsertRepeatedRow); }
                lockedRows.Add((pageId, slotIndex));
                var scope = new object?[oldValues.Length * 2];
                oldValues.CopyTo(scope, 0);
                proposal.Values.CopyTo(scope, oldValues.Length);
                if (!evaluator.Matches(plan.Where, scope)) { continue; }
                var updated = (object?[])oldValues.Clone();
                foreach (var (ordinal, expression) in plan.Assignments)
                {
                    updated[ordinal] = expression.Kind == SqlBoundExpressionKind.DefaultItem
                        ? ResolveColumnDefault(table, ordinal, bound.DefaultValues[ordinal], statement, cancellationToken, upsert: true)
                        : CoerceUpsertColumn(table, ordinal, evaluator.Evaluate(expression, scope, copySharedBinary: true));
                }
                if (assigned is not null) { CheckDomains(bound, updated, evaluator, assigned); }
                affected.Add(reference);
                write = new(reference, oldValues, updated, SqlRowCodec.Encode(table, updated, statement.Transaction.Sequence));
                foreach (var arbiter in arbiters)
                {
                    pending[(arbiter.Metadata.Name, BuildIndexKey(table, arbiter.KeyOrdinals, oldValues))] = null;
                }
            }
            else { write = new(null, null, proposal.Values, proposal.Record); }
            writes.Add(write);
            foreach (var arbiter in arbiters)
            {
                pending[(arbiter.Metadata.Name, BuildIndexKey(table, arbiter.KeyOrdinals, write.Values))] = write;
            }
        }

        await AcquireRowWriteLocksAsync(statement, table.ObjectId, lockedRows, cancellationToken).ConfigureAwait(false);
        var replacements = writes.Where(write => write.OldLocation.HasValue).ToList();
        SqlConstraintUpdates? pendingUpdates = null;
        if (replacements.Count > 0 && table.Constraints.Any(constraint => constraint.Kind == SqlCatalogConstraintKind.Reference &&
            string.Equals(constraint.ReferencedSchema, table.Schema, StringComparison.OrdinalIgnoreCase) &&
            string.Equals(constraint.ReferencedTable, table.Name, StringComparison.OrdinalIgnoreCase)))
        {
            // The existing referential-action view excludes replaced heap versions and contributes
            // their final values. An old self-referenced key must not authorize a new dangling child.
            var pendingRows = new Dictionary<(ulong ObjectId, ulong Location), SqlConstraintUpdate>();
            var assignedColumns = plan.Assignments.Select(assignment => assignment.Ordinal).ToHashSet();
            foreach (var write in replacements)
            {
                pendingRows.Add((table.ObjectId, write.OldLocation!.Value), new SqlConstraintUpdate(table,
                    SqlRecordLocation.Unpack(write.OldLocation.Value), write.OldValues!, write.Values, assignedColumns));
            }
            pendingUpdates = new SqlConstraintUpdates(pendingRows);
        }
        ValidateRows(table, replacements.Select(write => write.Values).ToList(), statement, cancellationToken, checksOnly: true);
        var references = new List<SqlParentReference>();
        // An RC arbiter wait can reach a new row and its parent committed together after the
        // original statement snapshot. Refresh committed visibility, never the all-writers
        // ConstraintCurrentSnapshot used by lock-protected referential walks and DDL.
        ValidateRows(table, writes.Select(write => write.Values).ToList(), statement, cancellationToken, references: references,
            updates: pendingUpdates, skipChecks: true,
            foreignKeySnapshot: statement.Transaction.IsolationLevel == IsolationLevel.ReadCommitted ? statement.Transaction.Snapshot : null);
        await AcquireParentRowLocksAsync(references, statement, cancellationToken).ConfigureAwait(false);
        var released = new List<SqlParentReference>();
        CollectReleasedReferences(table, replacements.Select(write => write.OldValues!).ToList(), released, statement, cancellationToken,
            changed: (constraint, index) => constraint.Columns.Any(column =>
            {
                int ordinal = FindColumnOrdinal(table, column);
                return !ValuesEqual(replacements[index].Values[ordinal], replacements[index].OldValues![ordinal]);
            }));
        await AcquireParentRowLocksAsync(released, statement, cancellationToken, validate: false).ConfigureAwait(false);
        hashes.Clear();
        foreach (var write in writes)
        {
            if (write.OldValues is not null)
            {
                ValidateParentUpdate(table, write.OldValues, write.Values, statement, cancellationToken, updates: pendingUpdates);
                CollectUniqueKeyHashes(indexes, table, write.OldValues, hashes);
            }
            CollectUniqueKeyHashes(indexes, table, write.Values, hashes);
        }
        await AcquireUniqueKeyLocksAsync(statement, table.ObjectId, hashes, cancellationToken).ConfigureAwait(false);
        var returned = returning is null ? null : new List<object?[]>(writes.Count);
        return await statement.Coordinator.ApplyStatementAsync(statement.Transaction, async bracket =>
        {
            foreach (var write in writes)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (write.OldLocation is { } oldReference)
                {
                    var (pageId, slotIndex) = SqlRecordLocation.Unpack(oldReference);
                    EnsureLatestVersion(table, pageId, slotIndex, statement.Transaction.Sequence);
                    TombstoneVersion(statement, bracket, pageId, slotIndex);
                    await TombstoneIndexEntriesAsync(statement, indexes, table, write.OldValues!, oldReference, cancellationToken).ConfigureAwait(false);
                }
                var location = _storage.InsertRow(bracket, table.ObjectId, write.Record);
                statement.Coordinator.VersionStore.RecordCreated(statement.Transaction.Sequence, location.PageId, location.SlotIndex);
                await InsertIndexEntriesAsync(statement, indexes, table, write.Values,
                    SqlRecordLocation.Pack(location.PageId, location.SlotIndex), cancellationToken).ConfigureAwait(false);
                if (returned is not null) { returned.Add(ProjectRow(write.Values, returning!, evaluator, cancellationToken)); }
            }
            return returned is null ? (DatabaseResult)new SqlQueryResult(DatabaseResultStatus.Success, writes.Count)
                : MaterializeProjected(returning!, returned, writes.Count);
        }, durable: false, cancellationToken).ConfigureAwait(false);

        TransactionAbortedException SnapshotConflict() => new(string.Format(CultureInfo.CurrentCulture,
            _upsertSnapshotConflict, table.Schema, table.Name));
    }

    private static object? CoerceUpsertColumn(SqlCatalogTable table, int ordinal, object? value)
    {
        var column = table.Columns[ordinal];
        if (value is null && !column.IsNullable)
        { throw new SqlConstraintViolationException(column.Name, $"{table.Schema}.{table.Name}", "NOT NULL"); }
        return CoerceForColumn(value, column);
    }
}

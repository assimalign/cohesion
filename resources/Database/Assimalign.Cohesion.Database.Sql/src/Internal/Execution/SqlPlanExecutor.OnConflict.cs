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
        IEnumerable<(byte[] Record, object?[] Values)> proposals, SqlOnConflictPlan plan, IReadOnlyList<SqlProjection>? returning,
        SqlStatementContext statement, bool orderedDefaults, CancellationToken cancellationToken)
    {
        var locks = statement.Coordinator.LockManager;
        var owner = statement.Transaction.Sequence;
        var statementMark = locks.MarkStatement(owner);
        var mark = statementMark;
        bool applyStarted = false;
        try
        {
            var arbiters = indexes.Where(index => plan.Arbiters.Any(arbiter => arbiter.Name == index.Metadata.Name)).ToList();
            var unique = indexes.Where(index => index.Metadata.IsUnique).ToList();
        RetryArbitration:
            cancellationToken.ThrowIfCancellationRequested();
            var hashes = new List<ulong>();
            // Build each proposal's keys once, sharing them between locking, arbitration and
            // pending inserts. Without side-effecting defaults the full batch precedes lookup.
            IEnumerable<(byte[] Record, object?[] Values, IndexKey[] Keys)> prepared = proposals.Select(proposal =>
                (proposal.Record, proposal.Values, unique.Select(index => BuildIndexKey(table, index.KeyOrdinals, proposal.Values)).ToArray()));
            if (!orderedDefaults)
            {
                prepared = prepared.ToList();
                foreach (var proposal in prepared) { hashes.AddRange(proposal.Keys.Select(key => key.Hash())); }
                await AcquireUniqueKeyLocksAsync(statement, table.ObjectId, hashes, cancellationToken).ConfigureAwait(false);
            }
            // Ordered defaults stay lazy: acquire each proposal's sorted unique keys after
            // evaluating its defaults and before latest-state arbitration or any row lock.
            // Reversed proposal orders can deadlock; the lock manager reports a retryable victim.
            var arbiterOrdinals = arbiters.Select(arbiter => unique.IndexOf(arbiter)).ToArray();
            var pending = new Dictionary<(string Index, IndexKey Key), SqlUpsertWrite?>();
            var affected = new HashSet<ulong>();
            var writes = new List<SqlUpsertWrite>();
            var evaluator = ExecutionEvaluator(_subqueryValues, cancellationToken);
            var bound = _definitions.Get(table, cancellationToken);
            int[]? assigned = bound.Domains.Count == 0 ? null : plan.Assignments.Select(assignment => assignment.Ordinal).ToArray();
            foreach (var proposal in prepared)
            {
                cancellationToken.ThrowIfCancellationRequested();
                // PostgreSQL nodeModifyTable.c:1108: proposed constraints precede arbitration.
                ValidateRows(table, [proposal.Values], statement, cancellationToken, checksOnly: true);
                if (orderedDefaults)
                {
                    hashes.Clear();
                    hashes.AddRange(proposal.Keys.Select(key => key.Hash()));
                    await AcquireUniqueKeyLocksAsync(statement, table.ObjectId, hashes, cancellationToken).ConfigureAwait(false);
                }
                ulong? conflict = null;
                bool pendingConflict = false;
                for (int arbiterIndex = 0; arbiterIndex < arbiters.Count; arbiterIndex++)
                {
                    var arbiter = arbiters[arbiterIndex];
                    var key = proposal.Keys[arbiterOrdinals[arbiterIndex]];
                    if (pending.TryGetValue((arbiter.Metadata.Name, key), out var prior))
                    {
                        if (prior is null) { continue; } // An earlier update released this key.
                        pendingConflict = true;
                        break;
                    }
                    ulong? live = await arbiter.Index.FindLiveEntryAsync(statement.Transaction, key, cancellationToken).ConfigureAwait(false);
                    if (live is { } location) { conflict = location; break; }
                }

                SqlUpsertWrite write;
                if (pendingConflict)
                {
                    if (!plan.DoUpdate) { continue; }
                    throw new SqlPlanningException("COHSQLE036", ErrorMessages.UpsertRepeatedRow);
                }
                if (conflict is { } reference)
                {
                    var row = LockResource.Entry(table.ObjectId, reference);
                    // DO NOTHING needs only the arbiter key and the visibility check below
                    // (PostgreSQL nodeModifyTable.c:1207-1211). DO UPDATE locks before WHERE.
                    if (plan.DoUpdate && !locks.TryAcquire(owner, row, LockMode.Exclusive))
                    {
                        mark = await WaitForUpsertRowAsync(row, LockMode.Exclusive, mark, statement, table, cancellationToken).ConfigureAwait(false);
                        goto RetryArbitration;
                    }
                    var (pageId, slotIndex) = SqlRecordLocation.Unpack(reference);
                    if (!_storage.TryReadRecord(pageId, slotIndex, table.ObjectId, out var record)) { throw DamagedRow(); }
                    var oldValues = DecodeRow(record, pageId, slotIndex, table, bound.MissingValues, out var writer, out _);
                    if (oldValues is null) { throw DamagedRow(); }
                    if (statement.Transaction.IsolationLevel != IsolationLevel.ReadCommitted && !statement.Snapshot.IsVisible(writer))
                    { throw SnapshotConflict(); }
                    if (!plan.DoUpdate) { continue; }
                    if (affected.Contains(reference)) { throw new SqlPlanningException("COHSQLE036", ErrorMessages.UpsertRepeatedRow); }
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
                    ValidateRows(table, [updated], statement, cancellationToken, checksOnly: true);
                    affected.Add(reference);
                    write = new(reference, oldValues, updated, SqlRowCodec.Encode(table, updated, statement.Transaction.Sequence));
                    StorageCorruptionException DamagedRow() => new(pageId, string.Format(CultureInfo.CurrentCulture,
                        ErrorMessages.UpsertLiveRowDamaged, table.Schema, table.Name, slotIndex, (long)pageId));
                    for (int arbiterIndex = 0; arbiterIndex < arbiters.Count; arbiterIndex++)
                    {
                        var arbiter = arbiters[arbiterIndex];
                        pending[(arbiter.Metadata.Name, BuildIndexKey(table, arbiter.KeyOrdinals, oldValues))] = null;
                    }
                }
                else { write = new(null, null, proposal.Values, proposal.Record); }
                writes.Add(write);
                for (int arbiterIndex = 0; arbiterIndex < arbiters.Count; arbiterIndex++)
                {
                    var arbiter = arbiters[arbiterIndex];
                    var key = write.OldLocation.HasValue ? BuildIndexKey(table, arbiter.KeyOrdinals, write.Values)
                        : proposal.Keys[arbiterOrdinals[arbiterIndex]];
                    pending[(arbiter.Metadata.Name, key)] = write;
                }
            }

            if (!plan.DoUpdate && writes.Count == 0)
            {
                // Visibility has been checked under the arbiter keys. With no writes to
                // protect, new arbitration grants need not block UPDATE until transaction end.
                locks.ReleaseSince(mark);
                return returning is null ? new SqlQueryResult(DatabaseResultStatus.Success, 0)
                    : MaterializeProjected(returning, [], 0);
            }

            foreach (var write in writes)
            {
                if (write.OldValues is not null)
                {
                    ValidateIndexKeysAndCollectUniqueHashes(indexes, table, write.OldValues, hashes);
                }
                ValidateIndexKeysAndCollectUniqueHashes(indexes, table, write.Values, hashes);
            }
            // Arbitration takes sorted key locks, then try-acquires rows. A refused row
            // releases new keys before waiting (WaitForUpsertRowAsync). Size checks stay
            // inside this release scope and precede the coordinator gate and page latches.
            await AcquireUniqueKeyLocksAsync(statement, table.ObjectId, hashes, cancellationToken).ConfigureAwait(false);
            var replacements = writes.Where(write => write.OldLocation.HasValue).ToList();
            var pendingUpdates = BuildConstraintUpdates(table,
                replacements.Select(write => (write.OldLocation!.Value, write.OldValues!, write.Values)), plan.Assignments);
            var references = new List<SqlParentReference>();
            // An RC arbiter wait can reach a new row and its parent committed together after the
            // original statement snapshot. Refresh committed visibility, never the all-writers
            // ConstraintCurrentSnapshot used by lock-protected referential walks and DDL.
            ValidateRows(table, writes.Select(write => write.Values).ToList(), statement, cancellationToken, references: references,
                updates: pendingUpdates, skipChecks: true,
                foreignKeySnapshot: statement.Transaction.IsolationLevel == IsolationLevel.ReadCommitted ? statement.Transaction.Snapshot : null);
            if (ContendedUpsertParent(references, statement) is { } parent)
            {
                mark = await WaitForUpsertRowAsync(parent, LockMode.Shared, mark, statement, null, cancellationToken).ConfigureAwait(false);
                goto RetryArbitration;
            }
            await AcquireParentRowLocksAsync(references, statement, cancellationToken).ConfigureAwait(false);
            var released = new List<SqlParentReference>();
            CollectReleasedReferences(table, replacements.Select(write => write.OldValues!).ToList(), released, statement, cancellationToken,
                changed: (constraint, index) => constraint.Columns.Any(column =>
                {
                    int ordinal = FindColumnOrdinal(table, column);
                    return !ValuesEqual(replacements[index].Values[ordinal], replacements[index].OldValues![ordinal]);
                }));
            if (ContendedUpsertParent(released, statement) is { } releasedParent)
            {
                mark = await WaitForUpsertRowAsync(releasedParent, LockMode.Shared, mark, statement, null, cancellationToken).ConfigureAwait(false);
                goto RetryArbitration;
            }
            await AcquireParentRowLocksAsync(released, statement, cancellationToken, validate: false).ConfigureAwait(false);

            foreach (var write in writes)
            {
                if (write.OldValues is not null)
                {
                    ValidateParentUpdate(table, write.OldValues, write.Values, statement, cancellationToken, updates: pendingUpdates);
                }
            }
            var returned = returning is null ? null : new List<object?[]>(writes.Count);
            applyStarted = true;
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
        }
        catch when (!applyStarted)
        {
            // Every preparation failure must release its new grants, including rows kept
            // by completed waits. Earlier transaction work and any started apply stay locked.
            locks.ReleaseSince(statementMark);
            throw;
        }

        TransactionAbortedException SnapshotConflict() => new(string.Format(CultureInfo.CurrentCulture,
            _upsertSnapshotConflict, table.Schema, table.Name));
    }

    // No heap/index mutation or RETURNING projection precedes the apply below arbitration.
    // Release new proposal locks, wait through the detector, then discard all pending state.
    // Keep the waited row across every restart: releasing it would let two waiters hand it
    // back and forth forever. A fresh mark also preserves rows kept by earlier waits.
    private async ValueTask<LockStatementMark> WaitForUpsertRowAsync(LockResource row, LockMode mode, LockStatementMark mark,
        SqlStatementContext statement, SqlCatalogTable? conflictTable, CancellationToken cancellationToken)
    {
        var locks = statement.Coordinator.LockManager;
        locks.ReleaseSince(mark);
        await locks.AcquireAsync(statement.Transaction.Sequence, row, mode, cancellationToken).ConfigureAwait(false);
        var retained = locks.MarkStatement(statement.Transaction.Sequence);
        cancellationToken.ThrowIfCancellationRequested();
        if (conflictTable is not null && statement.Transaction.IsolationLevel != IsolationLevel.ReadCommitted)
        {
            // A committed rekey/delete must still abort Snapshot even if re-arbitration would
            // find no live index entry. Rollback has undone the deleter before releasing the row.
            var (pageId, slotIndex) = SqlRecordLocation.Unpack(row.EntryId);
            EnsureLatestVersion(conflictTable, pageId, slotIndex, statement.Transaction.Sequence);
        }
        return retained;
    }

    private static LockResource? ContendedUpsertParent(List<SqlParentReference> references, SqlStatementContext statement)
    {
        foreach (var reference in references)
        {
            var row = LockResource.Entry(reference.Parent.ObjectId, reference.EntryId);
            if (!statement.Coordinator.LockManager.TryAcquire(statement.Transaction.Sequence, row, LockMode.Shared))
            {
                return row;
            }
        }
        return null;
    }

    private static object? CoerceUpsertColumn(SqlCatalogTable table, int ordinal, object? value)
    {
        var column = table.Columns[ordinal];
        if (value is null && !column.IsNullable)
        { throw new SqlConstraintViolationException(column.Name, $"{table.Schema}.{table.Name}", "NOT NULL"); }
        return CoerceForColumn(value, column);
    }
}

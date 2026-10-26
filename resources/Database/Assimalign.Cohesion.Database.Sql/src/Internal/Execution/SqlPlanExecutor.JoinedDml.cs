using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

using Assimalign.Cohesion.Database.Execution;
using Assimalign.Cohesion.Database.Indexing;
using Assimalign.Cohesion.Database.Storage;

namespace Assimalign.Cohesion.Database.Sql.Internal;

internal sealed partial class SqlPlanExecutor
{
    // Keep association state out of the ordinary DML async state machines and apply closures.
    private async Task<DatabaseResult> ExecuteJoinedUpdateAsync(SqlUpdatePlan plan, SqlStatementContext statement, CancellationToken cancellationToken)
    {
        await AcquireDmlSourceIntentLocksAsync(plan.Source!.Associations, statement, cancellationToken).ConfigureAwait(false);
        await AcquireReferenceIntentLocksAsync(plan.Table, statement, cancellationToken).ConfigureAwait(false);
        var evaluator = ExecutionEvaluator(_subqueryValues, cancellationToken,
            Enumerable.Repeat(true, plan.Source!.Associations.Columns.Count).ToArray());
        var indexes = GetLiveIndexes(plan.Table, statement);
        var targets = new List<(PageId PageId, int SlotIndex, object?[] Values)>();

        var associations = await CollectDmlAssociationsAsync(
            plan.Source, statement, cancellationToken, rejectMultiple: true).ConfigureAwait(false);
        // Fully drain and dispose the candidate cursor before row-lock waits or writes.
        // This materialization also prevents Halloween revisits when SET moves an index key.
        targets.AddRange(associations.Values.Select(row => (row.PageId, row.SlotIndex, row.TargetValues)));

        var returned = plan.Returning is null ? null : new List<object?[]>(targets.Count);
        var replacements = new List<(PageId PageId, int SlotIndex, object?[] OldValues, object?[] NewValues, byte[] NewVersion)>(targets.Count);
        var bound = _definitions.Get(plan.Table, cancellationToken);

        // Only the columns the statement sets are assigned their domain (phase E3): listed once, not per row.
        int[]? assigned = bound.Domains.Count > 0 ? plan.Assignments.Select(static assignment => assignment.Ordinal).ToArray() : null;
        foreach (var (pageId, slotIndex, values) in targets)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var updated = (object?[])values.Clone();
            var input = associations[SqlRecordLocation.Pack(pageId, slotIndex)].Values;

            foreach (var (ordinal, expression) in plan.Assignments)
            {
                // Assignments evaluate against the ORIGINAL row (SQL semantics).
                updated[ordinal] = expression.Kind == SqlBoundExpressionKind.DefaultItem
                    ? ResolveColumnDefault(plan.Table, ordinal, bound.DefaultValues[ordinal], statement, cancellationToken)
                    : CoerceForColumn(evaluator.Evaluate(expression, input, copySharedBinary: true), plan.Table.Columns[ordinal]);
            }

            if (assigned is not null)
            {
                CheckDomains(bound, updated, evaluator, assigned);
            }

            replacements.Add((pageId, slotIndex, values, updated,
                SqlRowCodec.Encode(plan.Table, updated, statement.Transaction.Sequence)));
        }

        // Row locks come before the constraint reads, not after them: the
        // incoming-reference check below reads child tables in latest state, and
        // that is only sound while this statement holds the exclusive lock on
        // every row whose key it might be changing (the referential-locking note
        // in SqlPlanExecutor.Constraints.cs).
        await AcquireRowWriteLocksAsync(statement, plan.Table.ObjectId, targets.ConvertAll(t => (t.PageId, t.SlotIndex)), cancellationToken).ConfigureAwait(false);

        var references = new List<SqlParentReference>();
        var pendingUpdates = BuildConstraintUpdates(plan.Table,
            replacements.Select(row => (SqlRecordLocation.Pack(row.PageId, row.SlotIndex), row.OldValues, row.NewValues)), plan.Assignments);
        ValidateRows(plan.Table, replacements.Select(row => row.NewValues).ToList(), statement, cancellationToken,
            references: references, updates: pendingUpdates);
        await AcquireParentRowLocksAsync(references, statement, cancellationToken).ConfigureAwait(false);

        // A reassigned foreign key releases the row's old parent as well as taking
        // on the new one, so the old parent row needs the same shared lock. Keys
        // the statement leaves alone are already covered by the locks above.
        var released = new List<SqlParentReference>();
        CollectReleasedReferences(plan.Table, replacements.ConvertAll(row => row.OldValues), released, statement, cancellationToken,
            changed: (reference, index) => reference.ChildKey.Ordinals.Any(ordinal =>
            {
                return !ValuesEqual(replacements[index].NewValues[ordinal], replacements[index].OldValues[ordinal]);
            }));
        await AcquireParentRowLocksAsync(released, statement, cancellationToken, validate: false).ConfigureAwait(false);

        foreach (var replacement in replacements)
        {
            ValidateParentUpdate(plan.Table, replacement.OldValues, replacement.NewValues, statement, cancellationToken, updates: pendingUpdates);
        }

        // Unique key locks after the row locks (the lock-ordering rule): both the
        // old key (its entry is tombstoned) and the new key (its entry is
        // inserted) of every unique index.
        var uniqueKeyHashes = new List<ulong>();
        foreach (var (_, _, oldValues, newValues, _) in replacements)
        {
            CollectUniqueKeyHashes(indexes, plan.Table, oldValues, uniqueKeyHashes);
            ValidateIndexKeysAndCollectUniqueHashes(indexes, plan.Table, newValues, uniqueKeyHashes);
        }

        await AcquireUniqueKeyLocksAsync(statement, plan.Table.ObjectId, uniqueKeyHashes, cancellationToken).ConfigureAwait(false);

        try
        {
            return await statement.Coordinator.ApplyStatementAsync(statement.Transaction, async bracket =>
            {
                RecheckDmlTargets(plan.Table, associations, statement, cancellationToken);
                int applied = 0;
                foreach (var (pageId, slotIndex, oldValues, _, _) in replacements)
                {
                    if ((applied++ & 63) == 0) { cancellationToken.ThrowIfCancellationRequested(); }
                    EnsureLatestVersion(plan.Table, pageId, slotIndex, statement.Transaction.Sequence);

                    // MVCC update = version chain in the record space: tombstone the
                    // old version in place (same-length write — never relocates) and
                    // insert the new version, both stamped with this transaction's
                    // sequence. The index entries mirror the row versions exactly:
                    // the old location's entries get the same deleter, the new
                    // location gets fresh entries with the same writer.
                    TombstoneVersion(statement, bracket, pageId, slotIndex);
                    await TombstoneIndexEntriesAsync(
                        statement, indexes, plan.Table, oldValues, SqlRecordLocation.Pack(pageId, slotIndex), cancellationToken).ConfigureAwait(false);

                }
                // Release every replaced unique mapping before inserting replacements,
                // so statement-consistent key swaps do not fail on an old live entry.
                applied = 0;
                foreach (var (pageId, slotIndex, _, newValues, newVersion) in replacements)
                {
                    if ((applied++ & 63) == 0) { cancellationToken.ThrowIfCancellationRequested(); }
                    var location = _storage.InsertRow(bracket, plan.Table.ObjectId, newVersion);
                    statement.Coordinator.VersionStore.RecordCreated(statement.Transaction.Sequence, location.PageId, location.SlotIndex);
                    await InsertIndexEntriesAsync(
                        statement, indexes, plan.Table, newValues, SqlRecordLocation.Pack(location.PageId, location.SlotIndex), cancellationToken).ConfigureAwait(false);
                    if (returned is not null)
                    {
                        var output = DmlReturningRow(
                            associations[SqlRecordLocation.Pack(pageId, slotIndex)].Values, newValues);
                        returned.Add(ProjectRow(output, plan.Returning!, evaluator, cancellationToken));
                    }
                }

                return await statement.PrepareResultAsync(returned is null ? new SqlQueryResult(DatabaseResultStatus.Success, replacements.Count)
                    : MaterializeProjected(plan.Returning!, returned, replacements.Count), cancellationToken).ConfigureAwait(false);
            }, durable: false, cancellationToken).ConfigureAwait(false);
        }
        catch (IndexUniqueViolationException exception)
        {
            throw TranslateUniqueViolation(plan.Table, exception);
        }
    }

    private async Task<DatabaseResult> ExecuteJoinedDeleteAsync(SqlDeletePlan plan, SqlStatementContext statement, CancellationToken cancellationToken)
    {
        var locks = statement.Coordinator.LockManager;
        var statementMark = statement.Metrics.JoinedDmlStatement is { } scope
            ? scope.Mark : locks.MarkStatement(statement.Transaction.Sequence);
        var retryMark = statementMark;
        const int retainedWaitThreshold = 3;
        int refusals = 0;
        bool applyStarted = false;
        Dictionary<ulong, DmlAssociation>? associations = null;
        try
        {
            while (true)
            {
                try
                {
                    // A cascade back-off can release association IS grants. Re-take them
                    // on every attempt, even when volatile associations were already collected.
                    await AcquireDmlSourceIntentLocksAsync(statement.Metrics.JoinedDmlStatement?.Plan ?? plan.Source!.Associations,
                        statement, cancellationToken, cascade: true).ConfigureAwait(false);
                    await AcquireReferenceIntentLocksAsync(plan.Table, statement, cancellationToken, outgoing: false, cascade: true).ConfigureAwait(false);
                    var evaluator = ExecutionEvaluator(_subqueryValues, cancellationToken,
                        Enumerable.Repeat(true, plan.Source!.Associations.Columns.Count).ToArray());
                    var targets = new List<((PageId PageId, int SlotIndex) Location, object?[] Values)>();
                    associations ??= await CollectDmlAssociationsAsync(
                        plan.Source!, statement, cancellationToken, rejectMultiple: false).ConfigureAwait(false);
                    targets.AddRange(associations.Values.Select(row => ((row.PageId, row.SlotIndex), row.TargetValues)));

                    // Lock the directly targeted rows as one sorted batch before the cascade
                    // walk reads any child table: a parent row must be exclusively locked
                    // before its incoming references are read in latest state. The walk locks
                    // each row it discovers the same way, in discovery order — a transitive
                    // closure cannot be pre-sorted, so referential waits rely on the lock
                    // manager's deadlock detection rather than on a global ordering.
                    await AcquireRowWriteLocksAsync(statement, plan.Table.ObjectId, targets.ConvertAll(t => t.Location), cancellationToken, cascade: true).ConfigureAwait(false);

                    var returned = plan.Returning is null ? null : new List<object?[]>(targets.Count);
                    var returningTargets = plan.Returning is null ? null : targets.Select(target =>
                        SqlRecordLocation.Pack(target.Location.PageId, target.Location.SlotIndex)).ToHashSet();
                    var deletions = new Dictionary<(ulong ObjectId, ulong Location), SqlConstraintDelete>();
                    var scannedTables = new HashSet<ulong>();
                    var released = new List<SqlParentReference>();
                    foreach (var target in targets)
                    {
                        await CollectCascadeDeletesAsync(plan.Table, target.Location, target.Values, deletions, scannedTables, released,
                            statement, cancellationToken).ConfigureAwait(false);
                    }

                    // Every deleted row releases the references it held; the shared locks are
                    // what make a concurrent parent delete wait for this transaction to decide
                    // instead of reading the tombstones as absence.
                    await AcquireParentRowLocksAsync(released, statement, cancellationToken, validate: false, cascade: true).ConfigureAwait(false);

                    var updates = await CollectReferentialUpdatesAsync(deletions, scannedTables, statement, cancellationToken).ConfigureAwait(false);
                    ValidateRestrictedDeletes(deletions, updates, statement, cancellationToken);
                    await LockConstraintDeletesAsync(deletions.Values, statement, cancellationToken).ConfigureAwait(false);
                    return await statement.Coordinator.ApplyStatementAsync(statement.Transaction, async bracket =>
                    {
                        applyStarted = true;
                        if (associations is not null) { RecheckDmlTargets(plan.Table, associations, statement, cancellationToken); }
                        // Revalidate every target before applying anything. The bracket makes all
                        // tombstones, versions and index changes one rollback unit.
                        foreach (var update in updates.Values)
                        {
                            cancellationToken.ThrowIfCancellationRequested();
                            EnsureLatestVersion(update.Table, update.Location.PageId, update.Location.SlotIndex, statement.Transaction.Sequence);
                        }
                        int applied = 0;
                        foreach (var deletion in deletions.Values)
                        {
                            if ((applied++ & 63) == 0) { cancellationToken.ThrowIfCancellationRequested(); }
                            EnsureLatestVersion(deletion.Table, deletion.Location.PageId, deletion.Location.SlotIndex, statement.Transaction.Sequence);
                            TombstoneVersion(statement, bracket, deletion.Location.PageId, deletion.Location.SlotIndex);
                            await TombstoneIndexEntriesAsync(statement, GetLiveIndexes(deletion.Table, statement), deletion.Table, deletion.Values,
                                SqlRecordLocation.Pack(deletion.Location.PageId, deletion.Location.SlotIndex), cancellationToken).ConfigureAwait(false);
                            if (returned is not null && deletion.Table.ObjectId == plan.Table.ObjectId &&
                                returningTargets!.Contains(SqlRecordLocation.Pack(deletion.Location.PageId, deletion.Location.SlotIndex)))
                            {
                                var output = associations![
                                    SqlRecordLocation.Pack(deletion.Location.PageId, deletion.Location.SlotIndex)].Values;
                                returned.Add(ProjectRow(output, plan.Returning!, evaluator, cancellationToken));
                            }
                        }
                        foreach (var update in updates.Values)
                        {
                            cancellationToken.ThrowIfCancellationRequested();
                            TombstoneVersion(statement, bracket, update.Location.PageId, update.Location.SlotIndex);
                            await TombstoneIndexEntriesAsync(statement, GetLiveIndexes(update.Table, statement), update.Table, update.OldValues,
                                SqlRecordLocation.Pack(update.Location.PageId, update.Location.SlotIndex), cancellationToken).ConfigureAwait(false);
                        }
                        foreach (var update in updates.Values)
                        {
                            cancellationToken.ThrowIfCancellationRequested();
                            var location = _storage.InsertRow(bracket, update.Table.ObjectId, update.NewVersion);
                            statement.Coordinator.VersionStore.RecordCreated(statement.Transaction.Sequence, location.PageId, location.SlotIndex);
                            try
                            {
                                await InsertIndexEntriesAsync(statement, GetLiveIndexes(update.Table, statement), update.Table, update.NewValues,
                                    SqlRecordLocation.Pack(location.PageId, location.SlotIndex), cancellationToken).ConfigureAwait(false);
                            }
                            catch (IndexUniqueViolationException exception)
                            {
                                throw TranslateUniqueViolation(update.Table, exception);
                            }
                        }
                        return await statement.PrepareResultAsync(returned is null ? new SqlQueryResult(DatabaseResultStatus.Success, targets.Count)
                            : MaterializeProjected(plan.Returning!, returned, targets.Count), cancellationToken).ConfigureAwait(false);
                    }, durable: false, cancellationToken).ConfigureAwait(false);
                }
                catch (SqlCascadeLockRetry refused)
                {
                    // Bound release/restart starvation on a hot parent. From the third
                    // refusal onward, retain the closure and queue normally; hard cycles
                    // still fail through AcquireAsync's deadlock detector.
                    if (refusals < retainedWaitThreshold) { refusals++; }
                    if (refusals < retainedWaitThreshold) { locks.ReleaseSince(retryMark); }
                    await locks.AcquireAsync(statement.Transaction.Sequence, refused.Resource, refused.Mode, cancellationToken).ConfigureAwait(false);
                    // Keep every waited grant across future back-offs. Releasing it permits
                    // contenders to hand the same row back and forth forever (#1668).
                    retryMark = locks.MarkStatement(statement.Transaction.Sequence);
                    statement.References.ClearAfterCascadeRetry();
                    cancellationToken.ThrowIfCancellationRequested();
                }
            }
        }
        catch when (!applyStarted && !_storage.IsOffline)
        {
            // Unwritten failures release even the retained wait grants. Previous
            // statements and their upgrades survive; an offline owner keeps all locks.
            locks.ReleaseSince(statementMark);
            throw;
        }
    }

}

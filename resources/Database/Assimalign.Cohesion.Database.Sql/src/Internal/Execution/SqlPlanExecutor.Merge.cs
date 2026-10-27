using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

using Assimalign.Cohesion.Database.Execution;
using Assimalign.Cohesion.Database.Indexing;
using Assimalign.Cohesion.Database.Sql.Language;
using Assimalign.Cohesion.Database.Transactions;

namespace Assimalign.Cohesion.Database.Sql.Internal;

internal sealed partial class SqlPlanExecutor
{
    private sealed record MergeCandidate(DmlAssociation? Target, object?[] Values, SqlMergeMatchKind MatchKind);
    private sealed record MergeWrite(MergeCandidate Candidate, SqlMergeActionPlan Action, object?[]? Values);

    private async Task<DatabaseResult> ExecuteMergeAsync(SqlMergePlan plan, SqlStatementContext statement,
        CancellationToken cancellationToken)
    {
        var locks = statement.Coordinator.LockManager;
        var scope = statement.Metrics.JoinedDmlStatement!.Value;
        var retryMark = scope.Mark;
        int refusals = 0;
        List<MergeWrite>? writes = null;
        while (true)
        {
            try
            {
                await AcquireDmlSourceIntentLocksAsync(scope.Plan, statement, cancellationToken, cascade: true).ConfigureAwait(false);
                await AcquireReferenceIntentLocksAsync(plan.Table, statement, cancellationToken, cascade: true).ConfigureAwait(false);
                // Keep volatile selection/defaults across a referential lock back-off.
                writes ??= await SelectMergeActionsAsync(plan, statement, cancellationToken).ConfigureAwait(false);
                return await ApplyMergeAsync(plan, writes, statement, cancellationToken).ConfigureAwait(false);
            }
            catch (SqlCascadeLockRetry refused)
            {
                if (++refusals < 3) { locks.ReleaseSince(retryMark); }
                await locks.AcquireAsync(statement.Transaction.Sequence, refused.Resource, refused.Mode, cancellationToken).ConfigureAwait(false);
                retryMark = locks.MarkStatement(statement.Transaction.Sequence);
                statement.References.ClearAfterCascadeRetry();
            }
        }
    }

    private async Task<List<MergeCandidate>> CollectMergeCandidatesAsync(SqlMergePlan merge,
        SqlStatementContext statement, CancellationToken cancellationToken)
    {
        var plan = merge.Source.Associations;
        long readsBefore = statement.Metrics.RowReads;
        Dictionary<SqlDerivedRelation, List<object?[]>>? derivedRows = null;
        foreach (var binding in plan.Bindings)
        {
            if (binding.Source.Derived is not { } derived) { continue; }
            (derivedRows ??= new(ReferenceEqualityComparer.Instance))[derived] =
                await MaterializeRelationAsync(derived, statement, cancellationToken).ConfigureAwait(false);
        }
        if (plan.Bindings.Any(binding => binding.Source.View is not null)) { _ = statement.RequireCatalogSnapshot(); }
        var identities = new Dictionary<object?[], DmlAssociation>(ReferenceEqualityComparer.Instance);
        foreach (var (location, values, writer) in EnumerateDmlTargetRows(merge.Table,
            plan.Inputs[merge.Source.TargetBinding].Access, statement, cancellationToken))
        {
            identities.Add(values, new(location.PageId, location.SlotIndex, writer, values, values));
            statement.Metrics.RecordDmlTargetRows(identities.Count);
        }
        var candidates = new List<MergeCandidate>();
        await foreach (var row in EnumerateJoinRowsAsync(plan, statement, new bool[plan.Columns.Count], cancellationToken,
            derivedRows, (merge.Source.TargetBinding, identities.Keys.ToArray())).ConfigureAwait(false))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var target = row[^2] is object?[] identity ? identities[identity] : null;
            var kind = target is null ? SqlMergeMatchKind.NotMatchedByTarget
                : row[^1] is null ? SqlMergeMatchKind.NotMatchedBySource : SqlMergeMatchKind.Matched;
            candidates.Add(new(target, row, kind));
        }
        statement.Metrics.DmlAssociationRowReads = statement.Metrics.RowReads - readsBefore;
        return candidates;
    }

    private async Task<List<MergeWrite>> SelectMergeActionsAsync(SqlMergePlan plan, SqlStatementContext statement,
        CancellationToken cancellationToken)
    {
        // Drain every association before selecting or writing, preventing Halloween revisits.
        var candidates = await CollectMergeCandidatesAsync(plan, statement, cancellationToken).ConfigureAwait(false);
        var parent = _subqueryValues;
        var frames = plan.Actions.Select(action => action.Queries.Count == 0 ? null
            : new SqlSubqueryValues(parent, action.Queries[0].ScopeId, action.Queries, lazy: true)).ToArray();
        var writes = new List<MergeWrite>();
        var modified = new HashSet<ulong>();
        var bound = _definitions.Get(plan.Table, cancellationToken);
        var shared = Enumerable.Repeat(true, plan.Source.Associations.Columns.Count + 2).ToArray();
        try
        {
            foreach (var candidate in candidates)
            {
                for (int i = 0; i < plan.Actions.Count; i++)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    var action = plan.Actions[i];
                    if (action.MatchKind != candidate.MatchKind) { continue; }
                    _subqueryValues = frames[i] ?? parent;
                    var evaluator = ExecutionEvaluator(_subqueryValues, cancellationToken, shared);
                    await PrepareSubqueriesAsync(action.Condition, candidate.Values, statement, cancellationToken).ConfigureAwait(false);
                    if (!evaluator.Matches(action.Condition, candidate.Values)) { continue; }
                    if (action.Action == SqlMergeAction.DoNothing) { break; }
                    // PostgreSQL nodeModifyTable.c:3521-3551: only an attempted second
                    // modification fails; extra matches handled by DO NOTHING are harmless.
                    if (candidate.Target is { } target && !modified.Add(SqlRecordLocation.Pack(target.PageId, target.SlotIndex)))
                    { throw new SqlExecutionException("CS05036", ErrorMessages.MergeRepeatedRow); }
                    object?[]? values = null;
                    if (action.Action == SqlMergeAction.Update)
                    {
                        values = (object?[])candidate.Target!.TargetValues.Clone();
                        foreach (var (ordinal, expression) in action.Assignments)
                        {
                            await PrepareSubqueriesAsync(expression, candidate.Values, statement, cancellationToken).ConfigureAwait(false);
                            values[ordinal] = expression.Kind == SqlBoundExpressionKind.DefaultItem
                                ? ResolveColumnDefault(plan.Table, ordinal, bound.DefaultValues[ordinal], statement, cancellationToken)
                                : CoerceForColumn(evaluator.Evaluate(expression, candidate.Values, copySharedBinary: true), plan.Table.Columns[ordinal]);
                        }
                        CheckDomains(bound, values, evaluator, action.Assignments.Select(assignment => assignment.Ordinal).ToArray());
                    }
                    else if (action.Action == SqlMergeAction.Insert)
                    {
                        var proposed = new object?[action.InsertValues.Count];
                        for (int index = 0; index < proposed.Length; index++)
                        {
                            var expression = action.InsertValues[index];
                            if (expression.Kind == SqlBoundExpressionKind.DefaultItem) { continue; }
                            await PrepareSubqueriesAsync(expression, candidate.Values, statement, cancellationToken).ConfigureAwait(false);
                            proposed[index] = evaluator.Evaluate(expression, candidate.Values, copySharedBinary: true);
                        }
                        values = new object?[plan.Table.Columns.Count];
                        var assigned = new bool[values.Length];
                        // As for INSERT, reserve generated identity before required-column validation.
                        for (int ordinal = 0; ordinal < values.Length; ordinal++)
                        {
                            if (plan.Table.Columns[ordinal].Identity is null) { continue; }
                            int index = -1;
                            for (int slot = 0; slot < action.InsertOrdinals.Count; slot++)
                            { if (action.InsertOrdinals[slot] == ordinal) { index = slot; break; } }
                            if (action.IsDefaultValues || index < 0 || action.InsertValues[index].Kind == SqlBoundExpressionKind.DefaultItem)
                            {
                                values[ordinal] = ResolveColumnDefault(plan.Table, ordinal, bound.DefaultValues[ordinal], statement, cancellationToken);
                                assigned[ordinal] = true;
                            }
                        }
                        for (int index = 0; index < action.InsertValues.Count; index++)
                        {
                            var expression = action.InsertValues[index];
                            if (expression.Kind == SqlBoundExpressionKind.DefaultItem) { continue; }
                            int ordinal = action.InsertOrdinals[index];
                            values[ordinal] = CoerceForColumn(proposed[index], plan.Table.Columns[ordinal]);
                            assigned[ordinal] = true;
                        }
                        for (int ordinal = 0; ordinal < values.Length; ordinal++)
                        {
                            if (!assigned[ordinal]) { values[ordinal] = ResolveColumnDefault(plan.Table, ordinal, bound.DefaultValues[ordinal], statement, cancellationToken); }
                        }
                        CheckDomains(bound, values, evaluator);
                    }
                    writes.Add(new(candidate, action, values));
                    break;
                }
            }
        }
        finally { _subqueryValues = parent; }
        return writes;
    }

    private async Task<DatabaseResult> ApplyMergeAsync(SqlMergePlan plan, List<MergeWrite> writes,
        SqlStatementContext statement, CancellationToken cancellationToken)
    {
        var targets = writes.Where(write => write.Candidate.Target is not null).Select(write => write.Candidate.Target!).ToArray();
        await AcquireRowWriteLocksAsync(statement, plan.Table.ObjectId, targets.Select(row => (row.PageId, row.SlotIndex)).ToList(),
            cancellationToken, cascade: true).ConfigureAwait(false);
        var deletions = new Dictionary<(ulong ObjectId, ulong Location), SqlConstraintDelete>();
        var scanned = new HashSet<ulong>();
        var released = new List<SqlParentReference>();
        foreach (var write in writes.Where(write => write.Action.Action == SqlMergeAction.Delete))
        {
            var target = write.Candidate.Target!;
            await CollectCascadeDeletesAsync(plan.Table, (target.PageId, target.SlotIndex), target.TargetValues,
                deletions, scanned, released, statement, cancellationToken).ConfigureAwait(false);
        }
        await AcquireParentRowLocksAsync(released, statement, cancellationToken, validate: false, cascade: true).ConfigureAwait(false);
        var cascades = await CollectReferentialUpdatesAsync(deletions, scanned, statement, cancellationToken).ConfigureAwait(false);
        var updates = cascades.Values.ToDictionary(update => (update.Table.ObjectId, SqlRecordLocation.Pack(update.Location.PageId, update.Location.SlotIndex)));
        foreach (var write in writes.Where(write => write.Action.Action == SqlMergeAction.Update))
        {
            var target = write.Candidate.Target!;
            var key = (plan.Table.ObjectId, SqlRecordLocation.Pack(target.PageId, target.SlotIndex));
            if (deletions.ContainsKey(key) || updates.ContainsKey(key)) { throw new SqlExecutionException("CS05036", ErrorMessages.MergeRepeatedRow); }
            updates.Add(key, new(plan.Table, (target.PageId, target.SlotIndex), target.TargetValues, write.Values!,
                write.Action.Assignments.Select(assignment => assignment.Ordinal).ToHashSet()));
        }
        var pending = new SqlConstraintUpdates(updates);
        ValidateRestrictedDeletes(deletions, pending, statement, cancellationToken);
        var references = new List<SqlParentReference>();
        ValidateRows(plan.Table, writes.Where(write => write.Values is not null).Select(write => write.Values!).ToList(),
            statement, cancellationToken, references: references, deletions: deletions, updates: pending);
        await AcquireParentRowLocksAsync(references, statement, cancellationToken, cascade: true).ConfigureAwait(false);
        released.Clear();
        foreach (var update in updates.Values)
        {
            ValidateParentUpdate(update.Table, update.OldValues, update.NewValues, statement, cancellationToken, deletions, pending);
            CollectReleasedReferences(update.Table, [update.OldValues], released, statement, cancellationToken);
            update.NewVersion = SqlRowCodec.Encode(update.Table, update.NewValues, statement.Transaction.Sequence);
        }
        await AcquireParentRowLocksAsync(released, statement, cancellationToken, validate: false, cascade: true).ConfigureAwait(false);
        await LockConstraintDeletesAsync(deletions.Values, statement, cancellationToken).ConfigureAwait(false);
        var indexes = GetLiveIndexes(plan.Table, statement);
        var hashes = new List<ulong>();
        foreach (var target in targets) { CollectUniqueKeyHashes(indexes, plan.Table, target.TargetValues, hashes); }
        foreach (var write in writes)
        {
            if (write.Values is { } values) { ValidateIndexKeysAndCollectUniqueHashes(indexes, plan.Table, values, hashes); }
        }
        await AcquireUniqueKeyLocksAsync(statement, plan.Table.ObjectId, hashes, cancellationToken, cascade: true).ConfigureAwait(false);
        var returned = plan.Returning is null ? null : new List<object?[]>(writes.Count);
        var evaluator = ExecutionEvaluator(_subqueryValues, cancellationToken,
            Enumerable.Repeat(true, plan.ReturningActionOrdinal + 1).ToArray());
        try
        {
            return await statement.Coordinator.ApplyStatementAsync(statement.Transaction, async bracket =>
            {
                foreach (var target in targets) { EnsureLatestVersion(plan.Table, target.PageId, target.SlotIndex, statement.Transaction.Sequence); }
                foreach (var deletion in deletions.Values)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    EnsureLatestVersion(deletion.Table, deletion.Location.PageId, deletion.Location.SlotIndex, statement.Transaction.Sequence);
                    TombstoneVersion(statement, bracket, deletion.Location.PageId, deletion.Location.SlotIndex);
                    await TombstoneIndexEntriesAsync(statement, GetLiveIndexes(deletion.Table, statement), deletion.Table, deletion.Values,
                        SqlRecordLocation.Pack(deletion.Location.PageId, deletion.Location.SlotIndex), cancellationToken).ConfigureAwait(false);
                }
                // Release old mappings before publishing replacements, as in joined UPDATE.
                foreach (var update in updates.Values)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    EnsureLatestVersion(update.Table, update.Location.PageId, update.Location.SlotIndex, statement.Transaction.Sequence);
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
                    catch (IndexUniqueViolationException exception) { throw TranslateUniqueViolation(update.Table, exception); }
                }
                foreach (var write in writes)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    if (write.Action.Action == SqlMergeAction.Insert)
                    {
                        var location = _storage.InsertRow(bracket, plan.Table.ObjectId, SqlRowCodec.Encode(plan.Table, write.Values!, statement.Transaction.Sequence));
                        statement.Coordinator.VersionStore.RecordCreated(statement.Transaction.Sequence, location.PageId, location.SlotIndex);
                        await InsertIndexEntriesAsync(statement, indexes, plan.Table, write.Values!,
                            SqlRecordLocation.Pack(location.PageId, location.SlotIndex), cancellationToken).ConfigureAwait(false);
                    }
                    if (returned is not null)
                    {
                        var output = new object?[plan.ReturningActionOrdinal + 1];
                        Array.Copy(write.Candidate.Values, output, plan.ReturningActionOrdinal);
                        (write.Values ?? write.Candidate.Target!.TargetValues).CopyTo(output, 0);
                        output[plan.ReturningActionOrdinal] = write.Action.Action.ToString().ToUpperInvariant();
                        returned.Add(ProjectRow(output, plan.Returning!, evaluator, cancellationToken));
                    }
                }
                return await statement.PrepareResultAsync(returned is null ? new SqlQueryResult(DatabaseResultStatus.Success, writes.Count)
                    : MaterializeProjected(plan.Returning!, returned, writes.Count), cancellationToken).ConfigureAwait(false);
            }, durable: false, cancellationToken).ConfigureAwait(false);
        }
        catch (IndexUniqueViolationException exception) { throw TranslateUniqueViolation(plan.Table, exception); }
    }
}

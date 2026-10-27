using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.ExceptionServices;
using System.Threading;
using System.Threading.Tasks;

using Assimalign.Cohesion.Database.Execution;
using Assimalign.Cohesion.Database.Indexing;
using Assimalign.Cohesion.Database.Sql.Language;
using Assimalign.Cohesion.Database.Storage;
using Assimalign.Cohesion.Database.Transactions;
using Assimalign.Cohesion.Database.Types;

namespace Assimalign.Cohesion.Database.Sql.Internal;

internal sealed partial class SqlPlanExecutor
{
    internal static Action<SqlStatementMetrics>? MergeActionsSelected { get; set; }
    internal static Action<SqlStatementMetrics>? MergeTargetRechecked { get; set; }

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
        var parent = _subqueryValues;
        var frames = plan.Actions.Select(action => action.Queries.Count == 0 ? null
            : new SqlSubqueryValues(parent, action.Queries[0].ScopeId, action.Queries, lazy: true)).ToArray();
        while (true)
        {
            try
            {
                await AcquireDmlSourceIntentLocksAsync(scope.Plan, statement, cancellationToken, cascade: true).ConfigureAwait(false);
                await AcquireReferenceIntentLocksAsync(plan.Table, statement, cancellationToken, cascade: true).ConfigureAwait(false);
                // Keep volatile selection/defaults across a referential lock back-off.
                if (writes is null)
                {
                    writes = await SelectMergeActionsAsync(plan, statement, cancellationToken, frames).ConfigureAwait(false);
                    MergeActionsSelected?.Invoke(statement.Metrics);
                }
                await RecheckMergeActionsAsync(plan, writes, statement, cancellationToken, frames).ConfigureAwait(false);
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
            var memory = statement.Metrics.DmlMemory(_definitions.StatementMemoryBudget)!;
            memory.Retain(values);
            memory.Reserve(128); // identity record, dictionary growth and target-input reference
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
            var memory = statement.Metrics.DmlMemory(_definitions.StatementMemoryBudget)!;
            memory.Retain(row);
            memory.Reserve(96); // candidate record and growing list
            candidates.Add(new(target, row, kind));
        }
        statement.Metrics.DmlAssociationRowReads = statement.Metrics.RowReads - readsBefore;
        return candidates;
    }

    private async Task<List<MergeWrite>> SelectMergeActionsAsync(SqlMergePlan plan, SqlStatementContext statement,
        CancellationToken cancellationToken, SqlSubqueryValues?[] frames, List<MergeCandidate>? candidates = null)
    {
        // Drain every association before selecting or writing, preventing Halloween revisits.
        candidates ??= await CollectMergeCandidatesAsync(plan, statement, cancellationToken).ConfigureAwait(false);
        var parent = _subqueryValues;
        var writes = new List<MergeWrite>();
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
                    var memory = statement.Metrics.DmlMemory(_definitions.StatementMemoryBudget)!;
                    if (values is not null) { memory.Retain(values); }
                    memory.Reserve(128); // write record, list and modified-target set
                    writes.Add(new(candidate, action, values));
                    break;
                }
            }
        }
        finally { _subqueryValues = parent; }
        return writes;
    }

    private async Task RecheckMergeActionsAsync(SqlMergePlan plan, List<MergeWrite> writes,
        SqlStatementContext statement, CancellationToken cancellationToken, SqlSubqueryValues?[] frames)
    {
        // Lock order: source/reference table intents, sorted original row locks, then
        // successor rows and referential rows, parent rows, unique keys, coordinator apply
        // gate, storage/index latches. All scans dispose before waits. Row grants use the
        // cascade release/retry policy; no wait holds the lineage monitor, gate or latch.
        await AcquireRowWriteLocksAsync(statement, plan.Table.ObjectId,
            writes.Where(write => write.Candidate.Target is not null)
                .Select(write => (write.Candidate.Target!.PageId, write.Candidate.Target.SlotIndex)).ToList(),
            cancellationToken, cascade: true).ConfigureAwait(false);
        for (int index = 0; index < writes.Count; index++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var original = writes[index].Candidate;
            if (original.Target is not { } target) { continue; }
            bool changed = false;
            while (true)
            {
                var (_, deleter) = ReadVersionStamps(plan.Table, target.PageId, target.SlotIndex);
                if (deleter == TransactionSequence.None || deleter == statement.Transaction.Sequence) { break; }
                if (statement.Transaction.IsolationLevel != IsolationLevel.ReadCommitted)
                { EnsureLatestVersion(plan.Table, target.PageId, target.SlotIndex, statement.Transaction.Sequence, codeSerialization: true); }
                changed = true;
                if (!_storage.RowVersionLinks.TryGet(SqlRecordLocation.Pack(target.PageId, target.SlotIndex), deleter, out var successor))
                { target = null; break; }
                var location = SqlRecordLocation.Unpack(successor);
                await AcquireRowWriteLocksAsync(statement, plan.Table.ObjectId, [location], cancellationToken, cascade: true).ConfigureAwait(false);
                statement.Metrics.RecordRowRead();
                var record = _storage.ReadRow(location.PageId, location.SlotIndex);
                var values = DecodeRow(record, location.PageId, location.SlotIndex, plan.Table,
                    _definitions.Get(plan.Table, cancellationToken).MissingValues, out var writer, out _);
                if (writer != deleter)
                {
                    var conflict = new TransactionAbortedException(string.Format(System.Globalization.CultureInfo.CurrentCulture,
                        ErrorMessages.DmlTargetVersionChanged, plan.Table.Schema, plan.Table.Name));
                    throw new DatabaseTransactionAbortedException(conflict.Message,
                        [new Diagnostic { Code = SqlExecutionException.SerializationFailureCode, Severity = DiagnosticSeverity.Error, Message = conflict.Message }], conflict);
                }
                target = new(location.PageId, location.SlotIndex, writer, values, values);
                var memory = statement.Metrics.DmlMemory(_definitions.StatementMemoryBudget)!;
                memory.Retain(values);
                memory.Reserve(128);
            }
            if (!changed) { continue; }
            MergeTargetRechecked?.Invoke(statement.Metrics);
            var candidates = new List<MergeCandidate>(2);
            if (target is null)
            {
                // PostgreSQL nodeModifyTable.c:3557-3577,3727-3744: a deleted
                // MATCHED target becomes BY TARGET; a deleted BY SOURCE row vanishes.
                if (original.MatchKind == SqlMergeMatchKind.Matched)
                { candidates.Add(Reframe(null, SqlMergeMatchKind.NotMatchedByTarget)); }
            }
            else
            {
                var current = Reframe(target, original.MatchKind);
                bool matched = original.MatchKind == SqlMergeMatchKind.NotMatchedBySource;
                if (!matched)
                {
                    await PrepareSubqueriesAsync(plan.Condition, current.Values, statement, cancellationToken).ConfigureAwait(false);
                    matched = ExecutionEvaluator(_subqueryValues, cancellationToken).Matches(plan.Condition, current.Values);
                }
                if (matched) { candidates.Add(current); }
                else
                {
                    // nodeModifyTable.c:3579-3724: EPQ restarts ordered WHEN selection.
                    // A broken match produces BOTH unmatched directions. BY SOURCE never
                    // becomes MATCHED, and new INSERT conflicts never re-arbitrate ON.
                    candidates.Add(Reframe(target, SqlMergeMatchKind.NotMatchedBySource));
                    candidates.Add(Reframe(null, SqlMergeMatchKind.NotMatchedByTarget));
                }
            }
            var selected = await SelectMergeActionsAsync(plan, statement, cancellationToken, frames, candidates).ConfigureAwait(false);
            // Persist completed rechecks across a later cascade-lock backoff, including
            // selected volatile expressions and generated defaults.
            writes.RemoveAt(index);
            writes.InsertRange(index, selected);
            index += selected.Count - 1;

            MergeCandidate Reframe(DmlAssociation? latest, SqlMergeMatchKind kind)
            {
                var values = (object?[])original.Values.Clone();
                if (latest is null) { Array.Clear(values, 0, plan.Table.Columns.Count); }
                else { latest.TargetValues.CopyTo(values, 0); }
                if (kind == SqlMergeMatchKind.NotMatchedBySource)
                { Array.Clear(values, plan.Table.Columns.Count, plan.Source.Associations.Columns.Count - plan.Table.Columns.Count); }
                statement.Metrics.DmlMemory(_definitions.StatementMemoryBudget)!.Retain(values);
                statement.Metrics.DmlMemory(_definitions.StatementMemoryBudget)!.Reserve(96);
                return new(latest, values, kind);
            }
        }
        var modified = new HashSet<ulong>();
        foreach (var write in writes)
        {
            // nodeModifyTable.c:3521-3551: only a second attempted direct modification
            // fails; DO NOTHING and referential actions do not count as direct actions.
            if (write.Candidate.Target is { } target && !modified.Add(SqlRecordLocation.Pack(target.PageId, target.SlotIndex)))
            { throw new SqlExecutionException("CS05036", ErrorMessages.MergeRepeatedRow); }
        }
    }

    private async Task<DatabaseResult> ApplyMergeAsync(SqlMergePlan plan, List<MergeWrite> writes,
        SqlStatementContext statement, CancellationToken cancellationToken)
    {
        var targets = writes.Where(write => write.Candidate.Target is not null).Select(write => write.Candidate.Target!).ToArray();
        await AcquireRowWriteLocksAsync(statement, plan.Table.ObjectId, targets.Select(row => (row.PageId, row.SlotIndex)).ToList(),
            cancellationToken, cascade: true).ConfigureAwait(false);
        // Row-local constraints precede AFTER-style referential actions, even if a
        // cascade subsequently removes the proposed row (nodeModifyTable.c:2327).
        ValidateRows(plan.Table, writes.Where(write => write.Values is not null).Select(write => write.Values!).ToList(),
            statement, cancellationToken, checksOnly: true);
        foreach (var write in writes)
        { if (write.Values is { } values) { _ = SqlRowCodec.Encode(plan.Table, values, statement.Transaction.Sequence); } }
        var deletions = new Dictionary<(ulong ObjectId, ulong Location), SqlConstraintDelete>();
        var updates = new Dictionary<(ulong ObjectId, ulong Location), SqlConstraintUpdate>();
        int insertion = 0;
        foreach (var write in writes.Where(write => write.Values is not null))
        {
            var target = write.Candidate.Target;
            // Negative pages are statement-local insert identities, never storage or lock
            // resources. Packing is injective over this budget-bounded statement's rows.
            var location = target is null ? (PageId: new PageId(-1L - insertion++), SlotIndex: 0) : (target.PageId, target.SlotIndex);
            var values = (object?[])write.Values!.Clone();
            updates.Add((plan.Table.ObjectId, SqlRecordLocation.Pack(location.PageId, location.SlotIndex)),
                new(plan.Table, location, target?.TargetValues ?? [], values,
                    write.Action.Assignments.Select(assignment => assignment.Ordinal).ToHashSet()));
            var memory = statement.Metrics.DmlMemory(_definitions.StatementMemoryBudget)!;
            memory.Retain(values);
            memory.Reserve(256L + 32L * write.Action.Assignments.Count);
        }
        SqlConstraintUpdates pending = new SqlMergeConstraintUpdates(updates, statement.Metrics.DmlMemory(_definitions.StatementMemoryBudget)!);
        var scanned = new HashSet<ulong>();
        var released = new List<SqlParentReference>();
        foreach (var write in writes.Where(write => write.Action.Action == SqlMergeAction.Delete))
        {
            var target = write.Candidate.Target!;
            await CollectCascadeDeletesAsync(plan.Table, (target.PageId, target.SlotIndex), target.TargetValues,
                deletions, scanned, released, statement, cancellationToken, pending).ConfigureAwait(false);
        }
        await AcquireParentRowLocksAsync(released, statement, cancellationToken, validate: false, cascade: true).ConfigureAwait(false);
        var cascadedUpdates = new List<SqlConstraintUpdate>();
        foreach (var key in deletions.Keys)
        {
            if (updates.Remove(key, out var update) && update.OldValues.Length != 0)
            {
                cascadedUpdates.Add(update);
                statement.Metrics.DmlMemory(_definitions.StatementMemoryBudget)!.Reserve(32);
            }
        }
        pending = await CollectReferentialUpdatesAsync(deletions, scanned, statement, cancellationToken, updates).ConfigureAwait(false);
        var references = new List<SqlParentReference>();
        released.Clear();
        ExceptionDispatchInfo? foreignKeyFailure = null;
        try
        {
            ValidateRestrictedDeletes(deletions, pending, statement, cancellationToken);
            // UPDATE's old-key NO ACTION check survives an AFTER-style cascade of its
            // proposed row (PostgreSQL ri_triggers.c:887,923-955).
            foreach (var update in cascadedUpdates)
            {
                ValidateParentUpdate(update.Table, update.OldValues, update.NewValues,
                    statement, cancellationToken, deletions, pending);
            }
            foreach (var group in updates.Values.GroupBy(update => update.Table))
            {
                var table = group.Key;
                foreach (var update in group)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    CheckDomains(_definitions.Get(table, cancellationToken), update.NewValues, ExecutionEvaluator(null, cancellationToken),
                        update.Location.PageId.Value < 0 ? null : update.Assigned.ToArray());
                    if (update.Location.PageId.Value >= 0)
                    {
                        ValidateParentUpdate(table, update.OldValues, update.NewValues, statement, cancellationToken, deletions, pending);
                        CollectReleasedReferences(table, [update.OldValues], released, statement, cancellationToken);
                    }
                    update.NewVersion = SqlRowCodec.Encode(table, update.NewValues, statement.Transaction.Sequence);
                    statement.Metrics.DmlMemory(_definitions.StatementMemoryBudget)!.Retain(update.NewVersion);
                }
                ValidateRows(table, group.Select(update => update.NewValues).ToList(), statement, cancellationToken,
                    references: references, deletions: deletions, updates: pending,
                    foreignKeySnapshot: statement.Transaction.IsolationLevel == IsolationLevel.ReadCommitted ? statement.Transaction.Snapshot : null);
            }
            }
        catch (SqlConstraintViolationException failure) when (failure.ConstraintKind == "FOREIGN KEY")
        {
            // Index uniqueness is immediate; FK triggers run after index insertion
            // (nodeModifyTable.c:2371-2383). Keep all lock waits outside the apply gate.
            foreignKeyFailure = ExceptionDispatchInfo.Capture(failure);
        }
        await AcquireParentRowLocksAsync(references, statement, cancellationToken, cascade: true).ConfigureAwait(false);
        await AcquireParentRowLocksAsync(released, statement, cancellationToken, validate: false, cascade: true).ConfigureAwait(false);
        await LockConstraintDeletesAsync(deletions.Values.Where(deletion => deletion.Location.PageId.Value >= 0), statement, cancellationToken).ConfigureAwait(false);
        foreach (var group in updates.Values.GroupBy(update => update.Table).OrderBy(group => group.Key.ObjectId))
        {
            var hashes = new List<ulong>();
            var live = GetLiveIndexes(group.Key, statement);
            foreach (var update in group)
            {
                if (update.Location.PageId.Value >= 0) { CollectUniqueKeyHashes(live, group.Key, update.OldValues, hashes); }
                ValidateIndexKeysAndCollectUniqueHashes(live, group.Key, update.NewValues, hashes);
            }
            await AcquireUniqueKeyLocksAsync(statement, group.Key.ObjectId, hashes, cancellationToken, cascade: true).ConfigureAwait(false);
        }
        var directIndexes = GetLiveIndexes(plan.Table, statement);
        var directHashes = new List<ulong>();
        foreach (var write in writes)
        {
            if (write.Values is not { } values) { continue; }
            ValidateIndexKeysAndCollectUniqueHashes(directIndexes, plan.Table, values, directHashes);
        }
        await AcquireUniqueKeyLocksAsync(statement, plan.Table.ObjectId, directHashes, cancellationToken, cascade: true).ConfigureAwait(false);
        var returned = plan.Returning is null ? null : new List<object?[]>(writes.Count);
        var evaluator = ExecutionEvaluator(_subqueryValues, cancellationToken,
            Enumerable.Repeat(true, plan.ReturningActionOrdinal + 1).ToArray());
        try
        {
            return await statement.Coordinator.ApplyStatementAsync(statement.Transaction, async bracket =>
            {
                // Immediate unique constraints see every direct proposal, including rows
                // that an AFTER-style cascade will remove. Direct UPDATE key swaps retain
                // the existing statement-level unique discipline shared with UPDATE FROM.
                var removed = targets.Select(target => SqlRecordLocation.Pack(target.PageId, target.SlotIndex)).ToHashSet();
                var proposedKeys = new HashSet<(string Index, IndexKey Key)>();
                foreach (var write in writes)
                {
                    if (write.Values is not { } values) { continue; }
                    foreach (var live in directIndexes.Where(index => index.Metadata.IsUnique))
                    {
                        cancellationToken.ThrowIfCancellationRequested();
                        var key = BuildIndexKey(plan.Table, live.KeyOrdinals, values);
                        statement.Metrics.DmlMemory(_definitions.StatementMemoryBudget)!.Reserve(128L + key.Length);
                        var conflict = await live.Index.FindLiveEntryAsync(statement.Transaction, key, cancellationToken).ConfigureAwait(false);
                        if (!proposedKeys.Add((live.Metadata.Name, key)) || conflict is { } location && !removed.Contains(location))
                        { throw TranslateUniqueViolation(plan.Table, new IndexUniqueViolationException(live.Metadata.Name, key)); }
                    }
                }
                foreignKeyFailure?.Throw();
                foreach (var target in targets) { EnsureLatestVersion(plan.Table, target.PageId, target.SlotIndex, statement.Transaction.Sequence, codeSerialization: true); }
                foreach (var deletion in deletions.Values.Where(deletion => deletion.Location.PageId.Value >= 0))
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    EnsureLatestVersion(deletion.Table, deletion.Location.PageId, deletion.Location.SlotIndex, statement.Transaction.Sequence, codeSerialization: true);
                    TombstoneVersion(statement, bracket, deletion.Location.PageId, deletion.Location.SlotIndex);
                    await TombstoneIndexEntriesAsync(statement, GetLiveIndexes(deletion.Table, statement), deletion.Table, deletion.Values,
                        SqlRecordLocation.Pack(deletion.Location.PageId, deletion.Location.SlotIndex), cancellationToken).ConfigureAwait(false);
                }
                // Release old mappings before publishing replacements, as in joined UPDATE.
                foreach (var update in updates.Values.Where(update => update.Location.PageId.Value >= 0))
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    EnsureLatestVersion(update.Table, update.Location.PageId, update.Location.SlotIndex, statement.Transaction.Sequence, codeSerialization: true);
                    TombstoneVersion(statement, bracket, update.Location.PageId, update.Location.SlotIndex);
                    await TombstoneIndexEntriesAsync(statement, GetLiveIndexes(update.Table, statement), update.Table, update.OldValues,
                        SqlRecordLocation.Pack(update.Location.PageId, update.Location.SlotIndex), cancellationToken).ConfigureAwait(false);
                }
                foreach (var update in updates.Values)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    var location = _storage.InsertRow(bracket, update.Table.ObjectId, update.NewVersion);
                    if (update.Location.PageId.Value >= 0)
                    { _storage.RowVersionLinks.Add(SqlRecordLocation.Pack(update.Location.PageId, update.Location.SlotIndex),
                        SqlRecordLocation.Pack(location.PageId, location.SlotIndex), statement.Transaction.Sequence); }
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

using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

using Assimalign.Cohesion.Database.Execution;
using Assimalign.Cohesion.Database.Indexing;
using Assimalign.Cohesion.Database.Sql.Catalog;
using Assimalign.Cohesion.Database.Storage;
using Assimalign.Cohesion.Database.Transactions;
using Assimalign.Cohesion.Database.Types;

namespace Assimalign.Cohesion.Database.Sql.Internal;

internal sealed partial class SqlPlanExecutor
{
    private bool HasUpdateActions(SqlCatalogTable table, IReadOnlyList<(int Ordinal, SqlBoundExpression Value)> assignments,
        bool changingOnly = true)
    {
        if (_catalog.ReferencedUpdateColumns(table, changingOnly) is not { Count: > 0 } columns) { return false; }
        for (int index = 0; index < assignments.Count; index++)
        { if (columns.Contains(table.Columns[assignments[index].Ordinal].Name)) { return true; } }
        return false;
    }

    private sealed record SqlUpdateActionState(SqlConstraintUpdates Inputs,
        Dictionary<(ulong, ulong, SqlCatalogConstraint), byte[]> Visited,
        Dictionary<(ulong, ulong, int), (ulong Parent, ulong Location, SqlCatalogConstraint Constraint)> Origins)
    {
        public long Generation { get; set; }
        public HashSet<(ulong, ulong, int)>? SharedOrigins { get; set; }
    }

    private bool HasChangingUpdateReferences(SqlCatalogTable table, SqlStatementContext statement, CancellationToken cancellationToken)
    {
        var incoming = IncomingReferences(table, statement, cancellationToken);
        for (int index = 0; index < incoming.Count; index++)
        {
            if (incoming[index].Constraint.OnUpdate is SqlCatalogReferentialAction.Cascade or SqlCatalogReferentialAction.SetNull or SqlCatalogReferentialAction.SetDefault)
            { return true; }
        }
        return false;
    }

    private async Task<SqlUpdateActionState?> ExpandUpdateActionsAsync(Dictionary<(ulong ObjectId, ulong Location), SqlConstraintUpdate> updates,
        Dictionary<(ulong ObjectId, ulong Location), SqlConstraintDelete> deletions, HashSet<ulong> scanned,
        SqlStatementContext statement, CancellationToken cancellationToken, SqlMergeConstraintUpdates? final,
        SqlUpdateActionState? state = null, IReadOnlyList<SqlConstraintUpdate>? removed = null)
    {
        // Keep ordinary DML's allocation contract: no worklist or projected copies unless
        // a reached table actually has a changing update action.
        bool needed = state is not null;
        if (!needed)
        {
            foreach (var row in updates.Values)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (HasChangingUpdateReferences(row.Table, statement, cancellationToken)) { needed = true; break; }
            }
        }
        if (!needed && removed is not null)
        { foreach (var row in removed) { if (HasChangingUpdateReferences(row.Table, statement, cancellationToken)) { needed = true; break; } } }
        if (!needed) { return null; }
        bool ownsState = state is null;
        try
        {
            var memory = statement.Metrics.DmlMemory(_definitions.StatementMemoryBudget);
            var inputs = state is null ? new Dictionary<(ulong ObjectId, ulong Location), SqlConstraintUpdate>() : null;
            if (inputs is not null)
            {
                foreach (var (key, row) in updates)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    var values = (object?[])row.NewValues.Clone();
                    inputs.Add(key, row with { NewValues = values });
                    memory?.Retain(values);
                    memory?.Reserve(256);
                }
            }
            // Match the pre-action child state. A key swap must not feed one parent's
            // replacement into another parent's old-key action. Retaining these inputs
            // also makes conflicting diamond/cycle visits observable rather than order-dependent.
            if (state is null)
            {
                SqlConstraintUpdates projectedInputs = memory is null ? new SqlConstraintUpdates(inputs!)
                    : new SqlMergeConstraintUpdates(inputs!, memory);
                state = new SqlUpdateActionState(projectedInputs, [], []);
            }
            var inputRows = state.Inputs;
            var pending = new Queue<SqlConstraintUpdate>(updates.Values);
            if (removed is not null) { foreach (var row in removed) { pending.Enqueue(row); } }
            var visited = state.Visited;
            foreach (var row in pending)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (row.Location.PageId.Value >= 0)
                { ValidateParentUpdate(row.Table, row.OldValues, row.NewValues, statement, cancellationToken, deletions, final, restrictOnly: true); }
            }
            while (pending.TryDequeue(out var parent))
            {
                cancellationToken.ThrowIfCancellationRequested();
                object?[] original = parent.OldValues;
                if (parent.Location.PageId.Value < 0)
                {
                    // A pending INSERT can itself be re-keyed by an incoming action. Its
                    // initial proposal is the old stored image for downstream actions,
                    // although it still has no physical predecessor to lock/tombstone.
                    var identity = (parent.Table.ObjectId, SqlRecordLocation.Pack(parent.Location.PageId, parent.Location.SlotIndex));
                    if (!inputRows.TryGet(identity, out var proposal)) { continue; }
                    original = proposal.NewValues;
                    foreach (var edge in IncomingReferences(parent.Table, statement, cancellationToken))
                    {
                        if (edge.Constraint.OnUpdate != SqlCatalogReferentialAction.Restrict ||
                            !ReferencedKeyChanged(edge.ParentKey, original, parent.NewValues, statement.Metrics)) { continue; }
                        var oldKey = edge.ParentKey.Read(original);
                        if (FinalConstraintRows(edge.ChildKey, oldKey, statement, cancellationToken, deletions, inputRows,
                            ConstraintCurrentSnapshot(statement)).Any()) { throw Violation(edge.Child, edge.Constraint, oldKey); }
                    }
                }
                foreach (var reference in IncomingReferences(parent.Table, statement, cancellationToken))
                {
                    var constraint = reference.Constraint;
                    if (!ReferencedKeyChanged(reference.ParentKey, original, parent.NewValues, statement.Metrics)) { continue; }
                    var keys = reference.ParentKey.Read(original);
                    if (constraint.OnUpdate is SqlCatalogReferentialAction.Restrict or SqlCatalogReferentialAction.NoAction) { continue; }
                    var visit = (parent.Table.ObjectId, SqlRecordLocation.Pack(parent.Location.PageId, parent.Location.SlotIndex), constraint);
                    var keyImage = new DatabaseKeyWriter();
                    foreach (int ordinal in reference.ParentKey.Ordinals)
                    { SqlRowCodec.AppendValue(keyImage, parent.Table.Columns[ordinal].Type.Type, parent.NewValues[ordinal]); }
                    byte[] image = keyImage.WrittenSpan.ToArray();
                    if (visited.TryGetValue(visit, out var previous) && previous.AsSpan().SequenceEqual(image)) { continue; }
                    visited[visit] = image;
                    memory?.Retain(image);
                    memory?.Reserve(128);
                    // Materialize and dispose every storage cursor before lock waits.
                    var matches = CollectConstraintMatches(FinalConstraintRows(reference.ChildKey, keys, statement, cancellationToken,
                        deletions, inputRows, ConstraintCurrentSnapshot(statement)), statement);
                    foreach (var match in matches)
                    {
                        cancellationToken.ThrowIfCancellationRequested();
                        var child = reference.Child;
                        var key = (child.ObjectId, SqlRecordLocation.Pack(match.Location.PageId, match.Location.SlotIndex));
                        bool added = !updates.TryGetValue(key, out var update);
                        if (added)
                        {
                            await AcquireReferenceIntentLocksAsync(child, statement, cancellationToken, cascade: true).ConfigureAwait(false);
                            await AcquireRowWriteLocksAsync(statement, child.ObjectId, [match.Location], cancellationToken, cascade: true).ConfigureAwait(false);
                            EnsureSnapshotSeesCascadeRow(child, match.Location, statement, ReferentialActionText(constraint.OnUpdate), updating: true);
                            EnsureLatestVersion(child, match.Location.PageId, match.Location.SlotIndex, statement.Transaction.Sequence, codeSerialization: true);
                            scanned.Add(child.ObjectId);
                            update = new(child, match.Location, match.Values, (object?[])match.Values.Clone(), []);
                            updates.Add(key, update);
                            memory?.Retain(match.Values);
                            memory?.Retain(update.NewValues);
                            memory?.Reserve(384L + 32L * child.Columns.Count);
                        }
                        var childUpdate = update!;
                        using var sequenceRow = SqlSequenceExecution.Current?.BeginRow(childUpdate.SequenceValues);
                        var bound = _definitions.Get(child, cancellationToken);
                        bool changed = false;
                        for (int index = 0; index < reference.ChildKey.Ordinals.Length; index++)
                        {
                            int ordinal = reference.ChildKey.Ordinals[index];
                            object? value = constraint.OnUpdate switch
                            {
                                SqlCatalogReferentialAction.Cascade => parent.NewValues[reference.ParentKey.Ordinals[index]],
                                SqlCatalogReferentialAction.SetNull => null,
                                _ => ResolveColumnDefault(child, ordinal, bound.DefaultValues[ordinal], statement, cancellationToken),
                            };
                            value = CoerceForColumn(value, child.Columns[ordinal]);
                            bool equal = StoredAssignmentEqual(child.Columns[ordinal], childUpdate.NewValues[ordinal], value);
                            RecordActionProposal(state, key, ordinal, visit, child, childUpdate.NewValues[ordinal], value, memory);
                            changed |= !equal;
                            childUpdate.NewValues[ordinal] = value;
                            if (!equal) { childUpdate.Assigned.Add(ordinal); }
                        }
                        childUpdate.SequenceValues = SqlSequenceExecution.Current?.CaptureRow();
                        memory?.Retain(childUpdate.NewValues);
                        if (added || changed)
                        {
                            if (childUpdate.Location.PageId.Value >= 0)
                            { ValidateParentUpdate(child, childUpdate.OldValues, childUpdate.NewValues, statement, cancellationToken, deletions, final, restrictOnly: true); }
                            state.Generation++;
                            final?.Refresh(childUpdate, added);
                            pending.Enqueue(childUpdate);
                        }
                    }
                }
            }
            return state;
        }
        catch
        {
            // The caller cannot dispose a newly created state until expansion returns it.
            if (ownsState) { (state?.Inputs as IDisposable)?.Dispose(); }
            throw;
        }
    }

    private static void RecordActionProposal(SqlUpdateActionState state, (ulong, ulong) key, int ordinal,
        (ulong Parent, ulong Location, SqlCatalogConstraint Constraint) origin, SqlCatalogTable child,
        object? previousValue, object? value, SqlDmlMemory? memory)
    {
        var cell = (key.Item1, key.Item2, ordinal);
        if (!state.Origins.TryGetValue(cell, out var previous))
        {
            memory?.Reserve(128);
            state.Origins.Add(cell, origin);
            return;
        }
        // All admitted origins agree on the current stored value. Remember when more
        // than one origin owns it; only an exclusive origin may revise its proposal.
        if (previous != origin || state.SharedOrigins?.Contains(cell) == true)
        {
            if (!StoredAssignmentEqual(child.Columns[ordinal], previousValue, value))
            { throw SqlExecutionException.ReferentialAssignmentConflict(origin.Constraint.Name, child.Schema, child.Name, child.Columns[ordinal].Name); }
            if (previous != origin)
            {
                state.SharedOrigins ??= [];
                if (state.SharedOrigins.Add(cell)) { memory?.Reserve(128); }
            }
        }
    }

    // Assignment conflicts compare stored values, including byte arrays, padding and
    // floating representations. Action eligibility itself has only ReferencedKeyChanged.
    private static bool StoredAssignmentEqual(SqlCatalogColumn column, object? left, object? right)
    {
        var first = new DatabaseKeyWriter();
        var second = new DatabaseKeyWriter();
        SqlRowCodec.AppendValue(first, column.Type.Type, left);
        SqlRowCodec.AppendValue(second, column.Type.Type, right);
        return first.WrittenSpan.SequenceEqual(second.WrittenSpan);
    }

    private async Task<DatabaseResult> ExecuteReferentialUpdateAsync(SqlUpdatePlan plan, SqlStatementContext statement,
        CancellationToken cancellationToken)
    {
        var locks = statement.Coordinator.LockManager;
        var initialMark = statement.Metrics.JoinedDmlStatement is { } scope ? scope.Mark : locks.MarkStatement(statement.Transaction.Sequence);
        var retryMark = initialMark;
        int refusals = 0;
        bool applying = false;
        Dictionary<(ulong ObjectId, ulong Location), SqlConstraintUpdate>? seeds = null;
        Dictionary<ulong, DmlAssociation>? associations = null;
        try
        {
            while (true)
            {
                try
                {
                    if (plan.Source is not null)
                    { await AcquireDmlSourceIntentLocksAsync(statement.Metrics.JoinedDmlStatement?.Plan ?? plan.Source.Associations, statement, cancellationToken, cascade: true).ConfigureAwait(false); }
                    await AcquireReferenceIntentLocksAsync(plan.Table, statement, cancellationToken, cascade: true).ConfigureAwait(false);
                    var evaluator = ExecutionEvaluator(_subqueryValues, cancellationToken,
                        plan.Source is null ? null : Enumerable.Repeat(true, plan.Source.Associations.Columns.Count).ToArray());
                    if (seeds is null)
                    {
                        seeds = [];
                        var targets = new List<((PageId PageId, int SlotIndex) Location, object?[] Values)>();
                        if (plan.Source is not null)
                        {
                            associations = await CollectDmlAssociationsAsync(plan.Source, statement, cancellationToken, rejectMultiple: true).ConfigureAwait(false);
                            targets.AddRange(associations.Values.Select(row => ((row.PageId, row.SlotIndex), row.TargetValues)));
                        }
                        else
                        {
                            foreach (var row in EnumerateRows(plan.Table, plan.Access, statement, cancellationToken))
                            {
                                await PrepareSubqueriesAsync(plan.Where, row.Values, statement, cancellationToken).ConfigureAwait(false);
                                if (evaluator.Matches(plan.Where, row.Values)) { targets.Add(row); }
                            }
                        }
                        var bound = _definitions.Get(plan.Table, cancellationToken);
                        foreach (var target in targets)
                        {
                            cancellationToken.ThrowIfCancellationRequested();
                            using var rowScope = SqlSequenceExecution.Current?.BeginRow();
                            var input = associations is null ? target.Values : associations[SqlRecordLocation.Pack(target.Location.PageId, target.Location.SlotIndex)].Values;
                            var values = (object?[])target.Values.Clone();
                            foreach (var (ordinal, expression) in plan.Assignments)
                            {
                                values[ordinal] = expression.Kind == SqlBoundExpressionKind.DefaultItem
                                    ? ResolveColumnDefault(plan.Table, ordinal, bound.DefaultValues[ordinal], statement, cancellationToken)
                                    : CoerceForColumn(evaluator.Evaluate(expression, input, copySharedBinary: true), plan.Table.Columns[ordinal]);
                            }
                            var update = new SqlConstraintUpdate(plan.Table, target.Location, target.Values, values,
                                plan.Assignments.Select(assignment => assignment.Ordinal).ToHashSet()) { SequenceValues = SqlSequenceExecution.Current?.CaptureRow() };
                            CheckDomains(bound, values, evaluator, update.Assigned.ToArray());
                            _ = SqlRowCodec.Encode(plan.Table, values, statement.Transaction.Sequence);
                            seeds.Add((plan.Table.ObjectId, SqlRecordLocation.Pack(target.Location.PageId, target.Location.SlotIndex)), update);
                            var memory = statement.Metrics.DmlMemory(_definitions.StatementMemoryBudget, referential: true);
                            memory?.Retain(target.Values);
                            memory?.Retain(values);
                            memory?.Reserve(384L + 32L * update.Assigned.Count);
                        }
                        ValidateRows(plan.Table, seeds.Values.Select(row => row.NewValues).ToList(), statement, cancellationToken, checksOnly: true);
                    }
                    // Sequence grants were taken at planning/execution admission, before any
                    // table lock. Adjacent tables are tried in ObjectId order, then own IX;
                    // sorted row grants precede incoming reads, parent shares and unique keys.
                    // Discovered-table inversions use #1420 release/retry then deadlock detection.
                    // The apply gate and storage/index latches are last; no latch spans a wait.
                    await AcquireRowWriteLocksAsync(statement, plan.Table.ObjectId, seeds.Values.Select(row => row.Location).ToList(), cancellationToken, cascade: true).ConfigureAwait(false);
                    foreach (var seed in seeds.Values)
                    {
                        cancellationToken.ThrowIfCancellationRequested();
                        EnsureLatestVersion(seed.Table, seed.Location.PageId, seed.Location.SlotIndex, statement.Transaction.Sequence, codeSerialization: true);
                    }
                    using var attempt = statement.Metrics.BeginReferentialAttempt(_definitions.StatementMemoryBudget);
                    var rows = seeds.ToDictionary(pair => pair.Key, pair => pair.Value with
                    { NewValues = (object?[])pair.Value.NewValues.Clone(), Assigned = new HashSet<int>(pair.Value.Assigned) });
                    foreach (var row in rows.Values) { statement.Metrics.DmlMemory(_definitions.StatementMemoryBudget)?.Retain(row.NewValues); }
                    var pending = await CollectReferentialUpdatesAsync([], [], statement, cancellationToken, rows, validateUpdates: true).ConfigureAwait(false);
                    var returned = plan.Returning is null ? null : new List<object?[]>();
                    if (returned is not null)
                    {
                        foreach (var key in seeds.Keys)
                        {
                            var row = seeds[key];
                            var output = associations is null ? row.NewValues : DmlReturningRow(associations[key.Location].Values, row.NewValues);
                            returned.Add(ProjectRow(output, plan.Returning!, evaluator, cancellationToken));
                        }
                    }
                    return await statement.Coordinator.ApplyStatementAsync(statement.Transaction, async bracket =>
                    {
                        applying = true;
                        if (associations is not null) { RecheckDmlTargets(plan.Table, associations, statement, cancellationToken); }
                        await ApplyConstraintUpdatesAsync(pending, statement, bracket, cancellationToken).ConfigureAwait(false);
                        return await statement.PrepareResultAsync(returned is null ? new SqlQueryResult(DatabaseResultStatus.Success, seeds.Count)
                            : MaterializeProjected(plan.Returning!, returned, seeds.Count), cancellationToken).ConfigureAwait(false);
                    }, durable: false, cancellationToken).ConfigureAwait(false);
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
        catch when (!applying && !_storage.IsOffline)
        {
            locks.ReleaseSince(initialMark);
            throw;
        }
        finally
        {
            if (statement.Metrics.JoinedDmlStatement is null) { statement.Metrics.ReleaseDmlMemory(); }
        }
    }

    private async Task ApplyConstraintUpdatesAsync(SqlConstraintUpdates updates, SqlStatementContext statement,
        StorageTransaction bracket, CancellationToken cancellationToken)
    {
        foreach (var row in updates.Values)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (row.Location.PageId.Value >= 0) { EnsureLatestVersion(row.Table, row.Location.PageId, row.Location.SlotIndex, statement.Transaction.Sequence, codeSerialization: true); }
        }
        foreach (var row in updates.Values)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (row.Location.PageId.Value < 0) { continue; }
            TombstoneVersion(statement, bracket, row.Location.PageId, row.Location.SlotIndex);
            await TombstoneIndexEntriesAsync(statement, GetLiveIndexes(row.Table, statement), row.Table, row.OldValues,
                SqlRecordLocation.Pack(row.Location.PageId, row.Location.SlotIndex), cancellationToken).ConfigureAwait(false);
        }
        foreach (var row in updates.Values)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var location = _storage.InsertRow(bracket, row.Table.ObjectId, row.NewVersion);
            if (row.Location.PageId.Value >= 0)
            { _storage.RowVersionLinks.Add(SqlRecordLocation.Pack(row.Location.PageId, row.Location.SlotIndex), SqlRecordLocation.Pack(location.PageId, location.SlotIndex), statement.Transaction.Sequence); }
            statement.Coordinator.VersionStore.RecordCreated(statement.Transaction.Sequence, location.PageId, location.SlotIndex);
            try
            { await InsertIndexEntriesAsync(statement, GetLiveIndexes(row.Table, statement), row.Table, row.NewValues, SqlRecordLocation.Pack(location.PageId, location.SlotIndex), cancellationToken).ConfigureAwait(false); }
            catch (IndexUniqueViolationException failure) { throw TranslateUniqueViolation(row.Table, failure); }
        }
    }
}

using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Runtime.ExceptionServices;
using System.Threading;
using System.Threading.Tasks;

using Assimalign.Cohesion.Database.Execution;
using Assimalign.Cohesion.Database.Types;

namespace Assimalign.Cohesion.Database.Sql.Internal;

internal sealed partial class SqlPlanExecutor
{
    [Flags]
    private enum RetentionCheck { Group = 1, Join = 2, BoundedBody = 4, Distinct = 8, OrderedDistinct = 16 }

    // Reuse the executor's optional relation-state slot: ordinary statements and ordinary
    // CTEs keep their exact object sizes. Only a correlated statement creates this map.
    private sealed class RetentionRelationRows : RelationRows
    {
        internal RetentionRelationRows(RelationRows? previous) : base(previous?.TransactionStart)
        {
            if (previous is not null)
            { foreach (var entry in previous) { Add(entry.Key, entry.Value); } Reserved = previous.Reserved; previous.Reserved = 0; }
        }

        internal Dictionary<SqlPlan, (RetentionCheck Checked, RetentionCheck Allowed)> Eligibility { get; }
            = new(ReferenceEqualityComparer.Instance);
    }

    private bool RetentionEligible(SqlPlan plan, RetentionCheck check)
    {
        RetentionRelationRows? cache = _materializedRelations as RetentionRelationRows;
        if (cache is null)
        {
            for (var frame = _subqueryValues; frame is not null && cache is null; frame = frame.Parent)
            {
                if (!frame.HasCorrelatedQueries) { continue; }
                foreach (var query in frame.Queries.Values)
                {
                    if (!query.IsCorrelated) { continue; }
                    cache = new RetentionRelationRows(_materializedRelations);
                    _materializedRelations = cache;
                    break;
                }
            }
        }
        var answer = cache is not null ? cache.Eligibility.GetValueOrDefault(plan) : default;
        if ((answer.Checked & check) != 0) { return (answer.Allowed & check) != 0; }
        _definitions.ObserveSelectRetention?.Invoke("eligibility-inspection", false, 0);
        bool allowed = check switch
        {
            RetentionCheck.Group => InspectGroupInput((SqlGroupPlan)plan),
            RetentionCheck.Join => InspectJoinOutput((SqlJoinPlan)plan),
            RetentionCheck.BoundedBody => InspectBoundedSubqueryBody(plan),
            RetentionCheck.OrderedDistinct => InspectPlainDistinct(plan, orderedInput: true),
            _ => InspectPlainDistinct(plan),
        };
        if (cache is not null)
        { cache.Eligibility[plan] = (answer.Checked | check, answer.Allowed | (allowed ? check : 0)); }
        return allowed;
    }

    private bool CanPipelineGroupInput(SqlGroupPlan plan)
        => CanPipelineSelectRetention() && RetentionEligible(plan, RetentionCheck.Group);

    private bool CanPipelineJoinOutput(SqlJoinPlan plan)
        => CanPipelineSelectRetention() && RetentionEligible(plan, RetentionCheck.Join);

    private bool CanBoundSubqueryBody(SqlSubqueryBinding query)
        => query.Kind is SqlSubqueryKind.Exists or SqlSubqueryKind.Scalar
            && CanPipelineSelectRetention() && RetentionEligible(query.Query, RetentionCheck.BoundedBody);

    private bool CanPipelineOrderedDistinct(SqlPlan plan)
        => plan is SqlSelectPlan { IsDistinct: true } && CanPipelineSelectRetention() && RetentionEligible(plan, RetentionCheck.OrderedDistinct);

    private bool CanPipelinePlainDistinct(SqlPlan plan)
        => (plan switch
        {
            SqlSelectPlan p => p.IsDistinct,
            SqlSystemViewPlan p => p.IsDistinct,
            SqlConstantPlan p => p.IsDistinct,
            _ => false
        })
            && CanPipelineSelectRetention() && RetentionEligible(plan, RetentionCheck.Distinct);

    private bool CanPipelineSelectRetention()
        => !_definitions.ForceSelectRetentionBasePath // Differential tests use the unchanged collectors.
            && RecursiveState is null // Recursive scratch retains its existing ownership and charges.
            && !_isDml // All write sources retain their existing DML ledger and evaluation.
            && SqlWindowMemory.For(_metrics) is null // A window's owner keeps its existing charges.
            && (_metrics is null || !SqlJsonPathExecution.IsStatement(_metrics)) // JSON scalar grants must not overlap new hash lifetimes.
            && _subqueryValues is not { HasCorrelatedQueries: true }; // Row preparation must remain staged.

    private bool InspectGroupInput(SqlGroupPlan plan)
    {
        try
        {
            // Every bound subquery runs within a frame. Ordinary plans need no subquery walk.
            bool inspectSubqueries = _subqueryValues is not null;
            // Subqueries retain their own staged setup, cache and lazy-branch preparation.
            if (!IsPlainRetentionInput(plan.Input) ||
                inspectSubqueries && (HasRetentionSubquery(plan.Input) || HasRetentionSubquery(plan.Having))) { return false; }
            bool downstreamVolatile = false;
            foreach (var key in plan.Keys)
            {
                if (!key.Collation.IsFixed || inspectSubqueries && HasRetentionSubquery(key.Value)) { return false; }
                downstreamVolatile |= RetentionHasVolatileCall(key.Value);
            }
            foreach (var aggregate in plan.Aggregates)
            {
                // Resolving a deferred collation must remain after complete input evaluation.
                if (!aggregate.Collation.IsFixed || inspectSubqueries && HasRetentionSubquery(aggregate.Filter)) { return false; }
                for (int i = 0; i < aggregate.ArgumentCollations.Count; i++)
                { if (!aggregate.ArgumentCollations[i].IsFixed) { return false; } }
                downstreamVolatile |= RetentionHasVolatileCall(aggregate.Filter) || aggregate.Function is not
                    (SqlCountFunction or SqlSumFunction or SqlExtremumFunction or SqlStringAggregateFunction or SqlStatisticsFunction);
                foreach (var argument in aggregate.Arguments)
                {
                    if (inspectSubqueries && HasRetentionSubquery(argument)) { return false; }
                    downstreamVolatile |= RetentionHasVolatileCall(argument);
                }
                foreach (var order in aggregate.OrderBy)
                {
                    if (!order.Collation.IsFixed || inspectSubqueries && HasRetentionSubquery(order.Key)) { return false; }
                    downstreamVolatile |= RetentionHasVolatileCall(order.Key);
                }
            }
            if (inspectSubqueries)
            {
                foreach (var projection in plan.Projections)
                { if (HasRetentionSubquery(projection.Value)) { return false; } }
                foreach (var order in plan.OrderBy)
                { if (HasRetentionSubquery(order.Key)) { return false; } }
            }
            // Materialization used to finish all predicates before aggregate transitions.
            // A later input error must not observe consumer calls that base never reached.
            return !downstreamVolatile && CanStreamRetentionTree(plan.Input);
        }
        catch (InsufficientExecutionStackException)
        {
            // Eligibility is optional; a shallow remaining stack must use the reference executor.
            return false;
        }
    }

    private bool InspectJoinOutput(SqlJoinPlan plan)
    {
        try
        {
            return (_subqueryValues is null || !HasRetentionSubquery(plan)) && CanStreamRetentionTree(plan)
                // Finish input before volatile outputs/keys, even when a later input row fails.
                && !RetentionOutputIsVolatile(plan)
                // Ordered DISTINCT must choose its representative after the stable sort.
                && (!plan.IsDistinct || InspectUnorderedDistinct(plan.Projections, plan.OrderBy));
        }
        catch (InsufficientExecutionStackException)
        {
            // Keep optional inspection from introducing a new statement-complexity refusal.
            return false;
        }
    }

    private bool InspectBoundedSubqueryBody(SqlPlan plan)
    {
        try
        {
            return plan is SqlSelectPlan or SqlJoinPlan
                && !HasRetentionSubquery(plan) // Includes independent children; call traversal omits their bodies.
                && !(RetentionInputIsVolatile(plan) && ((SqlRelationPlan)plan).OutputProjections
                    .Any(projection => RetentionHasVolatileCall(projection.Value))) // Preserve staged call order.
                && plan switch
                {
                    SqlSelectPlan scan => !scan.IsDistinct && scan.OrderBy.Count == 0,
                    SqlJoinPlan join => !join.IsDistinct && join.OrderBy.Count == 0 && InspectJoinOutput(join),
                    _ => false,
                };
        }
        catch (InsufficientExecutionStackException)
        {
            // Keep optional inspection from introducing a new statement-complexity refusal.
            return false;
        }
    }

    private static bool InspectUnorderedDistinct(IReadOnlyList<SqlProjection> projections, IReadOnlyList<SqlBoundOrdering> orderBy)
    {
        if (orderBy.Count != 0) { return false; } // Sort representatives stay on base.
        foreach (var projection in projections)
        {
            // Deferred collations use the last qualifying row, after all projections ran.
            if (!projection.Collation.IsFixed || HasRetentionSubquery(projection.Value)) { return false; }
        }
        return true;
    }

    private bool InspectPlainDistinct(SqlPlan plan, bool orderedInput = false)
    {
        try
        {
            return (plan switch
            {
                SqlSelectPlan p => p.IsDistinct,
                SqlSystemViewPlan p => p.IsDistinct,
                SqlConstantPlan p => p.IsDistinct,
                _ => false
            })
                && plan is SqlRelationPlan relation && !HasRetentionSubquery(plan)
                && !(RetentionInputIsVolatile(plan) && relation.OutputProjections.Any(projection => RetentionHasVolatileCall(projection.Value)))
                && plan switch
                {
                    SqlSelectPlan p => p.IsDistinct && InspectUnorderedDistinct(p.Projections, orderedInput ? [] : p.OrderBy),
                    SqlSystemViewPlan p => p.IsDistinct && InspectUnorderedDistinct(p.Projections, p.OrderBy),
                    SqlConstantPlan p => p.IsDistinct && InspectUnorderedDistinct(p.Projections, p.OrderBy),
                    _ => false,
                };
        }
        catch (InsufficientExecutionStackException)
        {
            // Keep optional inspection from introducing a new statement-complexity refusal.
            return false;
        }
    }

    private static bool IsPlainRetentionInput(SqlPlan plan) => plan switch
    {
        SqlSelectPlan p => !p.IsDistinct && p.OrderBy.Count == 0 && p.Limit is null && p.Offset is null,
        SqlJoinPlan p => !p.IsDistinct && p.OrderBy.Count == 0 && p.Limit is null && p.Offset is null,
        SqlSystemViewPlan p => !p.IsDistinct && p.OrderBy.Count == 0 && p.Limit is null && p.Offset is null,
        SqlConstantPlan p => !p.IsDistinct && p.OrderBy.Count == 0 && p.Limit is null && p.Offset is null,
        _ => false,
    };

    private static bool CanStreamRetentionTree(SqlPlan plan)
        // A tree completes ON before WHERE. A later ON error must not observe WHERE calls
        // that base never reached, even when ON itself contains no volatile function.
        => plan is not SqlJoinPlan { Tree: not null } join || !RetentionHasVolatileCall(join.Where);

    private static bool RetentionInputIsVolatile(SqlPlan plan) => plan switch
    {
        SqlSelectPlan p => RetentionHasVolatileCall(p.Where),
        SqlSystemViewPlan p => RetentionHasVolatileCall(p.Where),
        SqlConstantPlan p => RetentionHasVolatileCall(p.Where),
        SqlJoinPlan p => RetentionHasVolatileCall(p.Where) || p.Steps.Any(step => RetentionHasVolatileCall(step.Condition)) ||
            p.Inputs.Any(input => RetentionHasVolatileCall(input.Filter)),
        _ => true,
    };

    private static bool RetentionOutputIsVolatile(SqlJoinPlan plan)
        => plan.Projections.Any(projection => RetentionHasVolatileCall(projection.Value)) ||
            plan.OrderBy.Any(order => RetentionHasVolatileCall(order.Key));

    private static bool HasRetentionSubquery(SqlPlan plan)
    {
        if (plan is not SqlRelationPlan relation) { return true; }
        foreach (var projection in relation.OutputProjections)
        { if (HasRetentionSubquery(projection.Value)) { return true; } }
        return plan switch
        {
            SqlSelectPlan p => HasRetentionSubquery(p.Where) || p.OrderBy.Any(order => HasRetentionSubquery(order.Key)),
            SqlConstantPlan p => HasRetentionSubquery(p.Where) || p.OrderBy.Any(order => HasRetentionSubquery(order.Key)),
            SqlSystemViewPlan p => HasRetentionSubquery(p.Where) || p.OrderBy.Any(order => HasRetentionSubquery(order.Key)),
            SqlJoinPlan p => HasRetentionSubquery(p.Where) || p.OrderBy.Any(order => HasRetentionSubquery(order.Key)) ||
                p.Steps.Any(step => HasRetentionSubquery(step.Condition)) || p.Inputs.Any(input => HasRetentionSubquery(input.Filter)),
            _ => true,
        };
    }

    private static bool RetentionHasVolatileCall(SqlBoundExpression? expression)
    {
        if (expression is null || IsRetentionLeaf(expression)) { return false; }
        RuntimeHelpers.EnsureSufficientExecutionStack();
        if (expression is SqlBoundSequence or SqlBoundCall { Function.Volatility: SqlFunctionVolatility.Volatile }) { return true; }
        if (expression is SqlBoundCase choice)
        {
            if (RetentionHasVolatileCall(choice.Input) || RetentionHasVolatileCall(choice.ElseResult)) { return true; }
            foreach (var when in choice.Whens)
            { if (RetentionHasVolatileCall(when.Condition) || RetentionHasVolatileCall(when.Result)) { return true; } }
            return false;
        }
        if (expression is SqlBoundLogical logical) { return logical.Operands.Any(RetentionHasVolatileCall); }
        if (expression is SqlBoundCoalesce coalesce) { return coalesce.Arguments.Any(RetentionHasVolatileCall); }
        return BoundChildren(expression).Any(RetentionHasVolatileCall);
    }

    private static bool IsRetentionLeaf(SqlBoundExpression? expression)
        => expression is null or SqlBoundColumn or SqlBoundSlot or SqlBoundConstant or SqlBoundParameter
            or SqlBoundOuterColumn or SqlBoundGrouping;

    private static bool HasRetentionSubquery(SqlBoundExpression? expression)
    {
        RuntimeHelpers.EnsureSufficientExecutionStack();
        if (expression is null || IsRetentionLeaf(expression)) { return false; }
        if (expression is SqlBoundSubquery or SqlBoundInSubquery or SqlBoundQuantifiedComparison) { return true; }
        if (expression is SqlBoundCase choice)
        {
            if (HasRetentionSubquery(choice.Input) || HasRetentionSubquery(choice.ElseResult)) { return true; }
            foreach (var when in choice.Whens)
            { if (HasRetentionSubquery(when.Condition) || HasRetentionSubquery(when.Result)) { return true; } }
        }
        if (expression is SqlBoundLogical logical) { return logical.Operands.Any(HasRetentionSubquery); }
        if (expression is SqlBoundCoalesce coalesce) { return coalesce.Arguments.Any(HasRetentionSubquery); }
        return BoundChildren(expression).Any(HasRetentionSubquery);
    }

    private async Task<DatabaseResult> ExecuteBoundedSubqueryBodyAsync(SqlPlan plan, SqlStatementContext statement,
        CancellationToken cancellationToken, int retainRows)
    {
        // Only CanBoundSubqueryBody admits this one scan/join plan. Its enclosing ExecuteAsync
        // already established the JSON/recursive/DML scopes (all excluded by that predicate).
        // Keep the explicit argument out of the ordinary ExecuteAsync/Core state machines.
        var metrics = statement.Metrics;
        var frame = metrics.IsAnalyzing ? metrics.BeginAnalysisFrame() : null;
        try
        {
            RuntimeHelpers.EnsureSufficientExecutionStack();
            _containsLike |= plan.ContainsLike;
            _metrics = metrics;
            var initialized = InitializeWindow(plan, cancellationToken);
            var result = initialized is SqlSelectPlan scan
                ? ExecuteSelect(scan, statement, cancellationToken, retainRows)
                : await ExecutePipelinedJoinAsync((SqlJoinPlan)initialized, statement, cancellationToken, retainRows).ConfigureAwait(false);
            if (frame is not null)
            {
                if (plan is SqlSelectPlan)
                { metrics.Analysis(plan)!.Candidates += metrics.RecordsExamined - frame.StartRecords - frame.ChildRecords; }
                else if (plan is SqlJoinPlan { Steps.Count: > 0 })
                { metrics.Analysis(plan)!.Candidates += metrics.JoinCandidatePairs - frame.StartPairs - frame.ChildPairs; }
            }
            return result;
        }
        finally { if (frame is not null) { metrics.EndAnalysisFrame(frame); } }
    }

    private async IAsyncEnumerable<object?[]> EnumerateGroupInputAsync(SqlGroupPlan plan,
        SqlStatementContext statement, [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        // ExecuteAsync's JSON/recursive statement scopes already surround the group. Mirror its
        // per-plan setup and analysis frame for the pass-through child we no longer materialize.
        var metrics = statement.Metrics;
        var frame = metrics.IsAnalyzing ? metrics.BeginAnalysisFrame() : null;
        var input = plan.Input;
        try
        {
            RuntimeHelpers.EnsureSufficientExecutionStack();
            _containsLike |= plan.Input.ContainsLike;
            _metrics = metrics;
            input = InitializeWindow(plan.Input, cancellationToken);
            var projections = ((SqlRelationPlan)input).OutputProjections;
            if (input is SqlJoinPlan joined)
            {
                await foreach (var row in EnumerateJoinGroupInputAsync(joined, projections, statement, cancellationToken).ConfigureAwait(false))
                { yield return row; }
            }
            else
            {
                var evaluator = ExecutionEvaluator(_subqueryValues, cancellationToken);
                var candidates = input switch
                {
                    SqlSelectPlan scan => EnumerateRows(scan.Table, scan.Access, statement, cancellationToken).Select(row => row.Values),
                    SqlSystemViewPlan view => ReadCorrelatedSystemRelation(view.View, statement, cancellationToken),
                    _ => new[] { Array.Empty<object?>() },
                };
                var where = input switch
                { SqlSelectPlan scan => scan.Where, SqlSystemViewPlan view => view.Where, SqlConstantPlan constant => constant.Where, _ => null };
                foreach (var source in candidates)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    if (!evaluator.Matches(where, source)) { continue; }
                    yield return GroupInputRow(source, projections, evaluator, cancellationToken);
                }
            }
            if (frame is not null)
            {
                if (input is SqlSelectPlan)
                { metrics.Analysis(input)!.Candidates += metrics.RecordsExamined - frame.StartRecords - frame.ChildRecords; }
                else if (input is SqlJoinPlan { Steps.Count: > 0 })
                { metrics.Analysis(input)!.Candidates += metrics.JoinCandidatePairs - frame.StartPairs - frame.ChildPairs; }
            }
        }
        finally
        {
            _definitions.ObserveSelectRetention?.Invoke("group-input", true, 0);
            if (frame is not null) { metrics.EndAnalysisFrame(frame); }
        }
    }

    // Join-only reservation state must not enlarge every correlated scan iterator.
    private async IAsyncEnumerable<object?[]> EnumerateJoinGroupInputAsync(SqlJoinPlan joined,
        IReadOnlyList<SqlProjection> projections, SqlStatementContext statement,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        var (derived, derivedBytes) = await PrepareJoinInputsAsync(joined, statement, cancellationToken).ConfigureAwait(false);
        try
        {
            var shared = new bool[joined.Columns.Count];
            var evaluator = ExecutionEvaluator(_subqueryValues, cancellationToken, shared);
            await foreach (var source in EnumerateRetentionJoinRowsAsync(joined, statement, shared, derived,
                evaluator, cancellationToken).ConfigureAwait(false))
            {
                if (joined.Tree is null && !evaluator.Matches(joined.Where, source)) { continue; }
                yield return GroupInputRow(source, projections, evaluator, cancellationToken);
            }
        }
        finally { ReleaseSelectMemory(ref derivedBytes); }
    }

    private static object?[] GroupInputRow(object?[] source, IReadOnlyList<SqlProjection> projections,
        SqlExpressionEvaluator evaluator, CancellationToken cancellationToken)
    {
        var row = ProjectRow(source, projections, evaluator, cancellationToken);
        for (int i = 0; i < row.Length; i++)
        {
            // Reproduce GetValue's CHAR output followed by the consumer's typed input.
            // ProjectRow already detached reused binary columns through DeliverColumn/copySharedBinary.
            row[i] = SqlCharacter.Input(SqlCharacter.Output(row[i]), projections[i].Type);
        }
        return row;
    }

    private DatabaseResult ProjectUnorderedDistinct(IEnumerable<object?[]> matches, IReadOnlyList<SqlProjection> projections,
        SqlRowCount? limit, SqlRowCount? offset, SqlExpressionEvaluator evaluator, CancellationToken cancellationToken, SqlDatabaseSession? spillOwner = null)
    {
        SqlSelectMemory retainedBytes = default, seenBytes = default;
        HashSpill? spill = null;
        Exception? spillFailure = null;
        long ordinal = 0;
        try
        {
            _definitions.ObserveSelectRetention?.Invoke("distinct", true, 0);
            var collations = projections.Select(projection => projection.Collation.Resolve(evaluator.SubqueryValues)).ToArray();
            var seen = new HashSet<object?[]>(new GroupKeyComparer(collations));
            var rows = new List<object?[]>();
            ExceptionDispatchInfo? distinctFailure = null;
            foreach (var source in matches)
            {
                var output = ProjectRow(source, projections, evaluator, cancellationToken);
                long inputOrdinal = ordinal++;
                // Base projected every row before hashing. A collation/hash failure must not
                // suppress later projection calls; a projection failure still stops at its base row.
                if (distinctFailure is not null) { continue; }
                try
                {
                    if (!seen.Contains(output))
                    {
                        if (spill is null)
                        {
                            long previous = seenBytes.Bytes;
                            try
                            {
                                RetainSelectRow(ref seenBytes, output, overhead: 64);
                                RetainSelectRow(ref retainedBytes, output);
                                seen.Add(output); rows.Add(output);
                            }
                            catch (SqlExecutionException failure) when (failure.Diagnostics[0].Code == SqlExecutionException.StatementMemoryBudgetCode && spillOwner?.Database.Engine.TempSpace is not null)
                            {
                                if (seenBytes.Bytes != previous)
                                {
                                    long bytes = seenBytes.RemoveRow(output, 64); seenBytes.Bytes -= bytes;
                                    _metrics!.ReleasePlainSelectMemory(bytes);
                                }
                                spill = new HashSpill(this, spillOwner, collations);
                                spill.Append(output, [], inputOrdinal, cancellationToken);
                            }
                        }
                        else { spill.Append(output, [], inputOrdinal, cancellationToken); }
                    }
                }
                catch (Exception failure) when (CanDeferRetentionFailure(failure))
                { distinctFailure = ExceptionDispatchInfo.Capture(failure); }
            }
            cancellationToken.ThrowIfCancellationRequested();
            distinctFailure?.Throw();
            if (spill is not null)
            {
                spill.EmitDistinct(rows, cancellationToken);
                rows.Clear(); rows.Capacity = 0; seen.Clear(); seen.TrimExcess();
                ReleaseSelectMemory(ref retainedBytes); ReleaseSelectMemory(ref seenBytes);
                return spill.FinishDistinct(projections, limit, offset, cancellationToken);
            }
            _definitions.ObserveSelectRetention?.Invoke("distinct", true, rows.Count);
            return MaterializeProjected(projections, ApplyRowWindow(rows, limit, offset));
        }
        catch (Exception failure) { spillFailure = failure; throw; }
        finally { ReleaseSelectMemory(ref retainedBytes); ReleaseSelectMemory(ref seenBytes); spill?.Dispose(spillFailure); }
    }

    // As in grouped consumption, stack-guard failures wait for the base input drain.
    // Cancellation, OOM and statement memory refusal still abort immediately.
    private static bool CanDeferRetentionFailure(Exception failure)
        => failure is not (OperationCanceledException or OutOfMemoryException)
            && (failure is not SqlExecutionException sql || sql.Diagnostics[0].Code != SqlExecutionException.StatementMemoryBudgetCode);

    private IAsyncEnumerable<object?[]> EnumerateRetentionJoinRowsAsync(SqlJoinPlan plan, SqlStatementContext statement,
        bool[] sharedColumns, IReadOnlyDictionary<SqlDerivedRelation, List<object?[]>>? derived,
        SqlExpressionEvaluator evaluator, CancellationToken cancellationToken)
        => plan.Tree is not null && plan.Where is not null
            ? EnumerateFilteredRetentionTreeAsync(plan, statement, sharedColumns, derived, evaluator, cancellationToken)
            : EnumerateJoinRowsAsync(plan, statement, sharedColumns, cancellationToken, derived, streamRoot: true);

    private async IAsyncEnumerable<object?[]> EnumerateFilteredRetentionTreeAsync(SqlJoinPlan plan, SqlStatementContext statement,
        bool[] sharedColumns, IReadOnlyDictionary<SqlDerivedRelation, List<object?[]>>? derived,
        SqlExpressionEvaluator evaluator, [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        ExceptionDispatchInfo? whereFailure = null;
        await foreach (var source in EnumerateJoinRowsAsync(plan, statement, sharedColumns, cancellationToken, derived,
            streamRoot: true).ConfigureAwait(false))
        {
            // Base completed the root's ON work before its first WHERE. A WHERE failure
            // stops further predicates, but must still drain every remaining ON candidate.
            if (whereFailure is not null) { continue; }
            bool matches;
            try { matches = evaluator.Matches(plan.Where, source); }
            catch (Exception failure) when (CanDeferRetentionFailure(failure))
            { whereFailure = ExceptionDispatchInfo.Capture(failure); continue; }
            if (matches) { yield return source; }
        }
        whereFailure?.Throw();
    }

    private async ValueTask<DatabaseResult> ProjectJoinRowsAsync(SqlJoinPlan plan, SqlStatementContext statement,
        bool[] sharedColumns, IReadOnlyDictionary<SqlDerivedRelation, List<object?[]>>? derived,
        SqlExpressionEvaluator evaluator, CancellationToken cancellationToken, int? retainRows, SqlDatabaseSession? spillOwner = null, SqlGroupPlan? hashInput = null)
    {
        SqlSelectMemory retainedBytes = default, seenBytes = default;
        var rows = new List<object?[]>();
        try
        {
            var seen = plan.IsDistinct ? new HashSet<object?[]>(new GroupKeyComparer(plan.Projections
                .Select(projection => projection.Collation.Resolve(_subqueryValues)).ToArray())) : null;
            // Eligibility excludes window owners. Base did not create its collector until input
            // succeeded, so publish the ordered-row peak only after the complete input drain.
            using var collector = plan.OrderBy.Count == 0 ? null : new SqlRowCollector(plan.OrderBy, _subqueryValues,
                plan.Limit, plan.Offset, cancellationToken, statement.Metrics, SelectBufferBudget, publishPeak: false);
            long skip = plan.Offset?.Count ?? 0;
            long remaining = plan.Limit?.Count ?? long.MaxValue;
            ExceptionDispatchInfo? projectionFailure = null;
            ExceptionDispatchInfo? distinctFailure = null;
            _definitions.ObserveSelectRetention?.Invoke("join-matches", true, 0);
            if (seen is not null) { _definitions.ObserveSelectRetention?.Invoke("distinct", true, 0); }
            await foreach (var source in EnumerateRetentionJoinRowsAsync(plan, statement, sharedColumns, derived,
                evaluator, cancellationToken).ConfigureAwait(false))
            {
                cancellationToken.ThrowIfCancellationRequested();
                // Drain every candidate and WHERE, even after LIMIT (including LIMIT 0).
                if (plan.Tree is null && !evaluator.Matches(plan.Where, source)) { continue; }
                if (projectionFailure is not null) { continue; }
                try
                {
                    if (collector is null)
                    {
                        if (seen is null && remaining == 0) { continue; }
                        var output = ProjectRow(source, plan.Projections, evaluator, cancellationToken);
                        if (seen is not null)
                        {
                            if (distinctFailure is null)
                            {
                                try
                                {
                                    if (!seen.Contains(output))
                                    {
                                        RetainSelectRow(ref seenBytes, output, overhead: 64);
                                        RetainSelectRow(ref retainedBytes, output);
                                        seen.Add(output); rows.Add(output);
                                    }
                                }
                                catch (Exception failure) when (CanDeferRetentionFailure(failure))
                                { distinctFailure = ExceptionDispatchInfo.Capture(failure); }
                            }
                            continue;
                        }
                        if (skip > 0) { skip--; continue; }
                        if (retainRows is null || rows.Count < retainRows)
                        {
                            if (hashInput is null) { RetainSelectRow(ref retainedBytes, output); rows.Add(output); }
                            else if (RetainStagedSortSource(rows, output, ref retainedBytes, [], spillOwner, cancellationToken, hashInput: hashInput))
                            { rows.Add(output); }
                        }
                        remaining--;
                    }
                    else
                    {
                        if (collector.IsComplete) { continue; }
                        var output = ProjectRow(source, plan.Projections, evaluator, cancellationToken);
                        var ordered = source;
                        if (plan.OrderByProjections is { Count: > 0 })
                        {
                            ordered = new object?[source.Length + output.Length];
                            source.CopyTo(ordered, 0);
                            output.CopyTo(ordered, source.Length);
                        }
                        var keys = new object?[plan.OrderBy.Count];
                        for (int i = 0; i < keys.Length; i++) { keys[i] = evaluator.Evaluate(plan.OrderBy[i].Key, ordered); }
                        collector.Add(output, keys, spillOwner);
                    }
                }
                catch (Exception failure) when (CanDeferRetentionFailure(failure))
                {
                    // Base finished all input predicates before the first projection/key error.
                    // Keep draining those predicates, without evaluating further consumer expressions.
                    projectionFailure = ExceptionDispatchInfo.Capture(failure);
                }
            }
            if (collector is not null)
            { statement.Metrics.PeakOrderedRows = Math.Max(statement.Metrics.PeakOrderedRows, collector.RetainedCount); }
            projectionFailure?.Throw();
            distinctFailure?.Throw();
            if (collector is not null)
            {
                if (collector.HasSpilled) { return FinishCollected(collector, plan.Projections, plan.Limit, plan.Offset, false, evaluator, cancellationToken, resolveEmptyCollations: false); }
                rows = collector.Finish(resolveEmptyCollations: false);
            }
            if (seen is not null)
            {
                _definitions.ObserveSelectRetention?.Invoke("distinct", true, rows.Count);
                rows = ApplyRowWindow(rows, plan.Limit, plan.Offset);
            }
            if (retainRows is not null) { _definitions.ObserveSelectRetention?.Invoke("subquery-body", true, rows.Count); }
            if (hashInput is not null && SqlStagedSortSource.For(rows) is { } spooled)
            {
                spooled.Seal(cancellationToken);
                var input = new HashGroupInput(((DatabaseResultSet)MaterializeProjected(plan.Projections, [])).Columns, spooled);
                SqlStagedSortSource.Detach(rows); return input;
            }
            return MaterializeProjected(plan.Projections, rows);
        }
        catch (Exception failure) { if (hashInput is not null) { SqlStagedSortSource.ReleaseAfterFailure(rows, failure); } throw; }
        finally { if (hashInput is not null) { SqlStagedSortSource.Release(rows); } ReleaseSelectMemory(ref retainedBytes); ReleaseSelectMemory(ref seenBytes); }
    }
}

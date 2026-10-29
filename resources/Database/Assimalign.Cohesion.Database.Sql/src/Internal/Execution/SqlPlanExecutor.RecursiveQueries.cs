using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;

using Assimalign.Cohesion.Database.Execution;
using Assimalign.Cohesion.Database.Sql.Language;

namespace Assimalign.Cohesion.Database.Sql.Internal;

internal sealed partial class SqlPlanExecutor
{
    private async IAsyncEnumerable<object?[]> ReadRecursiveQueryAsync(SqlPlan plan, SqlSubqueryValues? frame,
        SqlStatementContext statement, [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        RuntimeHelpers.EnsureSufficientExecutionStack();
        cancellationToken.ThrowIfCancellationRequested();
        _containsLike |= plan.ContainsLike;
        if (plan is SqlWithPlan with)
        {
            await foreach (var row in ReadRecursiveQueryAsync(with.Input, frame, statement, cancellationToken).ConfigureAwait(false)) { yield return row; }
            yield break;
        }
        IReadOnlyList<SqlSubqueryBinding>? queries = plan switch
        { SqlSubqueryPlan p => p.Queries, SqlCorrelatedSubqueryPlan p => p.Queries, _ => null };
        if (queries is not null)
        {
            var child = new SqlSubqueryValues(frame, queries[0].ScopeId, queries, lazy: queries.Any(q => q.IsDeferred));
            var parent = _subqueryValues;
            _subqueryValues = child;
            try
            {
                foreach (var query in queries)
                {
                    if (query.IsCorrelated || query.IsDeferred) { continue; }
                    if (!child.StatementValues.TryGetValue(query.Slot, out var values))
                    {
                        values = await MaterializeSubqueryAsync(query, statement, cancellationToken).ConfigureAwait(false);
                        child.StatementValues.Add(query.Slot, values);
                    }
                    child.Set(query.Slot, values);
                }
            }
            finally { _subqueryValues = parent; }
            var input = plan is SqlSubqueryPlan subquery ? subquery.Input : ((SqlCorrelatedSubqueryPlan)plan).Input;
            try
            {
                await foreach (var row in ReadRecursiveQueryAsync(input, child, statement, cancellationToken).ConfigureAwait(false)) { yield return row; }
            }
            finally { child.ClearCorrelated(RecursiveState!.Memory); }
            yield break;
        }
        if (plan is SqlSetOperationPlan { Operator: SqlSetOperator.Union } union)
        {
            await foreach (var row in ReadRecursiveUnionOperandsAsync(union, frame, statement, cancellationToken).ConfigureAwait(false)) { yield return row; }
            yield break;
        }
        if (plan is SqlGroupPlan or SqlWindowAggregatePlan or SqlSetOperationPlan)
        {
            using var groupMemory = plan is SqlGroupPlan ? new SqlWindowMemory(statement.Metrics,
                _definitions.StatementMemoryBudget, statementBudgetOnly: true) : null;
            // These operators require completed inputs. Their child reads still use
            // the pull path, the captured statement snapshot and the shared grant.
            var parent = _subqueryValues;
            DatabaseResult result;
            _subqueryValues = frame;
            try
            {
                result = plan switch
                {
                    SqlGroupPlan group => await ExecuteGroupAsync(group, statement, cancellationToken).ConfigureAwait(false),
                    SqlWindowAggregatePlan window => await ExecuteWindowAggregateAsync(window, statement, cancellationToken).ConfigureAwait(false),
                    _ => await ExecuteSetOperationAsync((SqlSetOperationPlan)plan, statement, cancellationToken).ConfigureAwait(false),
                };
            }
            finally { _subqueryValues = parent; }
            await using ((DatabaseResultSet)result)
            {
                foreach (var row in ((SqlMaterializedResultSet)result).Rows)
                { cancellationToken.ThrowIfCancellationRequested(); yield return row; }
            }
            yield break;
        }
        var parts = plan switch
        {
            SqlConstantPlan p => (p.Projections, p.Where, p.OrderBy, p.Limit, p.Offset, p.IsDistinct, p.OrderByProjections),
            SqlSelectPlan p => (p.Projections, p.Where, p.OrderBy, p.Limit, p.Offset, p.IsDistinct, p.OrderByProjections),
            SqlSystemViewPlan p => (p.Projections, p.Where, p.OrderBy, p.Limit, p.Offset, p.IsDistinct, p.OrderByProjections),
            SqlJoinPlan p => (p.Projections, p.Where, p.OrderBy, p.Limit, p.Offset, p.IsDistinct, p.OrderByProjections),
            _ => throw new SqlExecutionException(SqlExecutionException.InternalErrorCode, ErrorMessages.QuerySourceRequired),
        };
        await foreach (var row in ReadRecursiveSelectAsync(plan, ReadRecursiveSourcesAsync(plan, frame, statement, cancellationToken),
            parts.Projections, parts.Where, parts.OrderBy, parts.Limit, parts.Offset, parts.IsDistinct, parts.OrderByProjections,
            frame, statement, cancellationToken).ConfigureAwait(false)) { yield return row; }
    }

    private async IAsyncEnumerable<object?[]> ReadRecursiveSelectAsync(SqlPlan plan, IAsyncEnumerable<object?[]> source,
        IReadOnlyList<SqlProjection> projections, SqlBoundExpression? where, IReadOnlyList<SqlBoundOrdering> ordering,
        SqlRowCount? limit, SqlRowCount? offset, bool distinct, IReadOnlyDictionary<SqlExpression, int>? outputSlots,
        SqlSubqueryValues? frame, SqlStatementContext statement, [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        var evaluator = ExecutionEvaluator(frame, cancellationToken);
        limit = limit?.Initialize(evaluator); offset = offset?.Initialize(evaluator);
        long remaining = limit?.Count ?? long.MaxValue;
        long skip = offset?.Count ?? 0;
        if (remaining == 0) { yield break; }
        using var seen = distinct ? new RecursiveDistinct(RecursiveState!.Memory,
            () => new GroupKeyComparer(projections.Select(p => p.Collation.Resolve(frame)).ToArray())) : null;
        // Sorting is blocking; a separate owner releases its scratch at this query's
        // end instead of retaining successive iterations' sort state.
        using var sortMemory = ordering.Count == 0 ? null : new SqlWindowMemory(statement.Metrics,
            _definitions.StatementMemoryBudget, statementBudgetOnly: true);
        var collector = ordering.Count == 0 ? null : new SqlRowCollector(ordering, frame,
            distinct ? null : limit, distinct ? null : offset, cancellationToken, statement.Metrics);
        bool deferred = collector is not null && !distinct &&
            (HasSequenceProjection(projections) && !OrderingUsesSequence(projections, outputSlots?.Values) ||
                outputSlots is not { Count: > 0 } && projections.Any(projection => ContainsVolatileCall(projection.Value)));
        var completed = deferred ? outputSlots?.Values.ToHashSet() : null;
        await foreach (var input in source.ConfigureAwait(false))
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (statement.Metrics.Analysis(plan) is { } work) { work.Candidates++; }
            await PrepareRecursiveExpressionAsync(where, input, frame, statement, cancellationToken).ConfigureAwait(false);
            if (!evaluator.Matches(where, input)) { continue; }
            for (int index = 0; index < projections.Count; index++)
            {
                if (!deferred || completed?.Contains(index) == true)
                { await PrepareRecursiveExpressionAsync(projections[index].Value, input, frame, statement, cancellationToken).ConfigureAwait(false); }
            }
            var output = deferred ? OrderingRow(input, projections, completed, evaluator, cancellationToken)
                : DetachRecursiveBinary(ProjectRow(input, projections, evaluator, cancellationToken), projections);
            if (seen is not null && !seen.Add(output)) { continue; }
            if (collector is not null)
            {
                var ordered = input;
                if (deferred) { ordered = output; }
                else if (outputSlots is { Count: > 0 })
                { ordered = new object?[input.Length + output.Length]; input.CopyTo(ordered, 0); output.CopyTo(ordered, input.Length); }
                var keys = new object?[ordering.Count];
                for (int index = 0; index < keys.Length; index++)
                {
                    await PrepareRecursiveExpressionAsync(ordering[index].Key, ordered, frame, statement, cancellationToken).ConfigureAwait(false);
                    keys[index] = evaluator.Evaluate(ordering[index].Key, ordered);
                }
                collector.Add(output, keys);
            }
            else
            {
                if (skip > 0) { skip--; continue; }
                yield return output;
                if (--remaining == 0) { yield break; }
            }
        }
        if (collector is not null)
        {
            var rows = collector.Finish(resolveEmptyCollations: false, includeOffset: deferred);
            if (deferred)
            {
                using var projected = new RecursiveRows(RecursiveState!.Memory);
                foreach (var ordered in rows)
                {
                    for (int index = 0; index < projections.Count; index++)
                    {
                        if (completed?.Contains(index) != true)
                        { await PrepareRecursiveExpressionAsync(projections[index].Value, ordered, frame, statement, cancellationToken).ConfigureAwait(false); }
                    }
                    projected.Add(DetachRecursiveBinary(ProjectRow(ordered, projections, evaluator, cancellationToken,
                        completed, completed is { Count: > 0 } ? ordered.Length - projections.Count : 0), projections));
                }
                int start = (int)Math.Min(offset?.Count ?? 0, projected.Count);
                int count = (int)Math.Min(limit?.Count ?? projected.Count, projected.Count - start);
                for (int index = start; index < start + count; index++)
                { cancellationToken.ThrowIfCancellationRequested(); yield return projected[index]; }
                yield break;
            }
            if (distinct) { rows = ApplyRowWindow(rows, limit, offset); }
            foreach (var row in rows) { cancellationToken.ThrowIfCancellationRequested(); yield return row; }
        }
    }

    private static object?[] DetachRecursiveBinary(object?[] output, IReadOnlyList<SqlProjection> projections)
    {
        // Independent readers can revisit a retained cell. Pass-through binary
        // delivery detaches it, as buffered joins do on the ordinary executor path.
        for (int index = 0; index < output.Length; index++)
        { if (projections[index].ColumnOrdinal is not null) { output[index] = SqlBinaryValue.CopyShared(output[index]); } }
        return output;
    }

    private async ValueTask PrepareRecursiveExpressionAsync(SqlBoundExpression? expression, object?[] row,
        SqlSubqueryValues? frame, SqlStatementContext statement, CancellationToken cancellationToken)
    {
        var parent = _subqueryValues; _subqueryValues = frame;
        try { await PrepareSubqueriesAsync(expression, row, statement, cancellationToken).ConfigureAwait(false); }
        finally { _subqueryValues = parent; }
    }

    private async Task<object?[]> MaterializeRecursiveSubqueryAsync(SqlSubqueryBinding query,
        SqlStatementContext statement, CancellationToken cancellationToken)
    {
        var memory = RecursiveState!.Memory;
        using var rows = new RecursiveRows(memory);
        bool found = false;
        await foreach (var row in ReadRecursiveQueryAsync(query.Query, new SqlSubqueryValues(_subqueryValues), statement, cancellationToken).ConfigureAwait(false))
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (query.Kind == SqlSubqueryKind.Scalar && found)
            { throw new SqlExecutionException(SqlExecutionException.CardinalityCode, ErrorMessages.ScalarSubqueryCardinality); }
            found = true;
            if (query.Kind == SqlSubqueryKind.Exists) { break; }
            rows.Add(row);
        }
        var values = query.Kind == SqlSubqueryKind.Exists ? new object?[] { query.IsNegated ? !found : found }
            : query.Kind == SqlSubqueryKind.Scalar && !found ? new object?[] { null } : new object?[rows.Count];
        for (int index = 0; index < rows.Count; index++) { values[index] = SqlBinaryValue.CopyShared(rows[index][0]); }
        memory.Retain(values);
        return values;
    }

    private async ValueTask<object?> EvaluateRecursiveSetAsync(SqlBoundExpression expression, SqlSubqueryBinding query,
        object?[] outer, SqlStatementContext statement, CancellationToken cancellationToken)
    {
        var frame = _subqueryValues!;
        var evaluator = ExecutionEvaluator(frame, cancellationToken);
        var member = expression as SqlBoundInSubquery;
        var quantified = expression as SqlBoundQuantifiedComparison;
        bool all = quantified?.All == true;
        bool unknown = false, found = false;
        object? value = null;
        await foreach (var row in ReadRecursiveQueryAsync(query.Query, new SqlSubqueryValues(frame), statement, cancellationToken).ConfigureAwait(false))
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!found)
            {
                var operand = member?.Operand ?? quantified!.Operand;
                await PrepareExpressionAsync(operand, outer, statement, cancellationToken).ConfigureAwait(false);
                value = EvaluatePrepared(operand, evaluator, outer);
                found = true;
            }
            // UNKNOWN cannot decide OR/AND. A later TRUE/FALSE can still win
            // (PostgreSQL src/backend/executor/nodeSubplan.c:399-427).
            if (value is null || row[0] is null) { unknown = true; continue; }
            int comparison = SqlExpressionEvaluator.Compare(value, row[0]!,
                (member?.Collation ?? quantified!.Collation).Resolve(frame));
            bool matches = (quantified?.Operator ?? SqlBinaryOperator.Equal) switch
            {
                SqlBinaryOperator.Equal => comparison == 0,
                SqlBinaryOperator.NotEqual => comparison != 0,
                SqlBinaryOperator.LessThan => comparison < 0,
                SqlBinaryOperator.LessOrEqual => comparison <= 0,
                SqlBinaryOperator.GreaterThan => comparison > 0,
                SqlBinaryOperator.GreaterOrEqual => comparison >= 0,
                _ => throw new SqlExecutionException(SqlExecutionException.InternalErrorCode, ErrorMessages.QuerySourceRequired),
            };
            if (matches != all) { return member?.IsNegated == true ? !matches : matches; }
        }
        return unknown ? null : member?.IsNegated == true ? !all : all;
    }

    private async IAsyncEnumerable<object?[]> ReadRecursiveUnionOperandsAsync(SqlSetOperationPlan plan,
        SqlSubqueryValues? frame, SqlStatementContext statement, [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        var evaluator = ExecutionEvaluator(frame, cancellationToken);
        using var seen = plan.IsAll ? null : new RecursiveDistinct(RecursiveState!.Memory,
            new GroupKeyComparer(plan.Projections.Select(p => p.Collation.Resolve(frame)).ToArray()));
        await foreach (var row in ReadRecursiveSelectAsync(plan, Operands(), plan.Projections, null, plan.OrderBy,
            plan.Limit, plan.Offset, false, null, frame, statement, cancellationToken).ConfigureAwait(false)) { yield return row; }

        async IAsyncEnumerable<object?[]> Operands()
        {
            foreach (var operand in plan.Operands)
            {
                await foreach (var source in ReadRecursiveQueryAsync(operand.Query,
                    frame is null ? null : new SqlSubqueryValues(frame), statement, cancellationToken).ConfigureAwait(false))
                {
                    var row = new object?[source.Length];
                    for (int index = 0; index < row.Length; index++)
                    { row[index] = SqlTemporalArithmetic.Promote(evaluator.Evaluate(operand.Values[index], source), plan.Projections[index].Type); }
                    if (seen is null || seen.Add(row)) { yield return row; }
                }
            }
        }
    }

    private async IAsyncEnumerable<object?[]> ReadRecursiveSourcesAsync(SqlPlan plan, SqlSubqueryValues? frame,
        SqlStatementContext statement, [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        if (plan is SqlJoinPlan join)
        {
            await foreach (var row in ReadRecursiveJoinAsync(join, frame, statement, cancellationToken).ConfigureAwait(false)) { yield return row; }
            yield break;
        }
        IEnumerable<object?[]> rows = plan switch
        {
            SqlConstantPlan => new[] { Array.Empty<object?>() },
            SqlSelectPlan select => EnumerateRows(select.Table, select.Access, statement, cancellationToken).Select(row => row.Values),
            SqlSystemViewPlan system => ReadCorrelatedSystemRelation(system.View, statement, cancellationToken),
            _ => [],
        };
        foreach (var row in rows) { cancellationToken.ThrowIfCancellationRequested(); yield return row; }
    }
}

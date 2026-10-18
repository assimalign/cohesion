using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Runtime.ExceptionServices;
using System.Threading;
using System.Threading.Tasks;

using Assimalign.Cohesion.Database.Execution;

namespace Assimalign.Cohesion.Database.Sql.Internal;

internal sealed partial class SqlPlanExecutor
{
    private IEnumerable<object?[]> ReadCorrelatedSystemRelation(SqlSystemViewDefinition view, SqlStatementContext statement,
        CancellationToken cancellationToken)
    {
        statement.Metrics.AccessPath = "system-view";
        foreach (var row in ReadSystemRelation(view, statement, cancellationToken))
        {
            cancellationToken.ThrowIfCancellationRequested();
            statement.Metrics.RecordsExamined++;
            yield return row;
        }
    }

    private ValueTask PrepareSubqueriesAsync(SqlBoundExpression? expression, object?[] row,
        SqlStatementContext statement, CancellationToken cancellationToken)
    {
        if (_subqueryValues is not { HasCorrelatedQueries: true } frame) { return ValueTask.CompletedTask; }
        frame.BeginRow(row);
        return expression is null ? ValueTask.CompletedTask : PrepareExpressionAsync(expression, row, statement, cancellationToken);
    }

    /// <summary>Awaits child operators before synchronous scalar evaluation, preserving lazy branches.</summary>
    private async ValueTask PrepareExpressionAsync(SqlBoundExpression expression, object?[] row,
        SqlStatementContext statement, CancellationToken cancellationToken)
    {
        RuntimeHelpers.EnsureSufficientExecutionStack();
        cancellationToken.ThrowIfCancellationRequested();
        var frame = _subqueryValues!;
        var evaluator = ExecutionEvaluator(frame, cancellationToken);
        int? slot = expression switch { SqlBoundSubquery query => query.Slot, SqlBoundInSubquery member => member.Slot, _ => null };
        if (slot is int id && frame.Queries.TryGetValue(id, out var binding) && !frame.TryGet(id, out _))
        {
            frame.Set(id, await MaterializeSubqueryAsync(binding, statement, cancellationToken).ConfigureAwait(false));
        }
        // Branch predicates are cached by the frame so volatile functions still run once.
        if (expression is SqlBoundLogical logical)
        {
            foreach (var operand in logical.Operands)
            {
                await PrepareExpressionAsync(operand, row, statement, cancellationToken).ConfigureAwait(false);
                var value = EvaluatePrepared(operand, evaluator, row);
                if (logical.IsOr ? value is true : value is false) { break; }
            }
            return;
        }
        if (expression is SqlBoundCoalesce coalesce)
        {
            foreach (var argument in coalesce.Arguments)
            {
                await PrepareExpressionAsync(argument, row, statement, cancellationToken).ConfigureAwait(false);
                if (EvaluatePrepared(argument, evaluator, row) is not null) { break; }
            }
            return;
        }
        if (expression is SqlBoundCase choice)
        {
            object? input = null;
            if (choice.Input is not null)
            {
                await PrepareExpressionAsync(choice.Input, row, statement, cancellationToken).ConfigureAwait(false);
                input = EvaluatePrepared(choice.Input, evaluator, row);
            }
            foreach (var when in choice.Whens)
            {
                await PrepareExpressionAsync(when.Condition, row, statement, cancellationToken).ConfigureAwait(false);
                var condition = EvaluatePrepared(when.Condition, evaluator, row);
                bool matches = choice.Input is null ? condition is true : input is not null && condition is not null &&
                    SqlExpressionEvaluator.Compare(input, condition, when.Collation.Resolve(frame)) == 0;
                if (matches)
                {
                    await PrepareExpressionAsync(when.Result, row, statement, cancellationToken).ConfigureAwait(false);
                    return;
                }
            }
            if (choice.ElseResult is not null)
            {
                await PrepareExpressionAsync(choice.ElseResult, row, statement, cancellationToken).ConfigureAwait(false);
            }
            return;
        }
        foreach (var child in BoundChildren(expression))
        {
            await PrepareExpressionAsync(child, row, statement, cancellationToken).ConfigureAwait(false);
        }
    }

    private object? EvaluatePrepared(SqlBoundExpression expression, SqlExpressionEvaluator evaluator, object?[] row)
    {
        var values = _subqueryValues!.Evaluated!;
        if (values.TryGetValue(expression, out var value)) { return value; }
        value = evaluator.Evaluate(expression, row);
        values.Add(expression, value);
        return value;
    }

    private static IEnumerable<SqlBoundExpression> BoundChildren(SqlBoundExpression expression)
    {
        switch (expression)
        {
            case SqlBoundBinary binary: yield return binary.Left; yield return binary.Right; break;
            case SqlBoundUnary unary: yield return unary.Operand; break;
            case SqlBoundIsNull isNull: yield return isNull.Operand; break;
            case SqlBoundBetween between:
                yield return between.LowOperand; yield return between.HighOperand;
                yield return between.Low; yield return between.High; break;
            case SqlBoundIn member:
                yield return member.Operand;
                foreach (var candidate in member.Candidates) { yield return candidate; }
                break;
            case SqlBoundInSubquery member: yield return member.Operand; break;
            case SqlBoundLike like: yield return like.Operand; yield return like.Pattern; break;
            case SqlBoundCall call:
                foreach (var argument in call.Arguments) { yield return argument; }
                break;
            case SqlBoundSpecialForm form:
                foreach (var argument in form.Arguments) { yield return argument; }
                break;
            case SqlBoundCast cast: yield return cast.Operand; break;
            case SqlBoundDomainArgument domain: yield return domain.Operand; break;
            case SqlBoundDefault value: yield return value.Value; break;
        }
    }

    private async Task<DatabaseResult> ExecuteCorrelatedSelectAsync(IEnumerable<object?[]> rows,
        IReadOnlyList<SqlProjection> projections, SqlBoundExpression? where, IReadOnlyList<SqlBoundOrdering> orderBy,
        SqlRowCount? limit, SqlRowCount? offset, bool distinct, SqlStatementContext statement, CancellationToken cancellationToken)
    {
        var evaluator = ExecutionEvaluator(_subqueryValues, cancellationToken);
        var matches = new List<object?[]>();
        foreach (var row in rows)
        {
            await PrepareSubqueriesAsync(where, row, statement, cancellationToken).ConfigureAwait(false);
            if (evaluator.Matches(where, row)) { matches.Add(row); }
        }
        return await MaterializeCorrelatedSelectAsync(matches, projections, orderBy, limit, offset, distinct,
            evaluator, statement, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Computes projection and sort values within each outer row's frame, then sorts fixed keys.</summary>
    private async Task<DatabaseResult> MaterializeCorrelatedSelectAsync(List<object?[]> matches,
        IReadOnlyList<SqlProjection> projections, IReadOnlyList<SqlBoundOrdering> orderBy, SqlRowCount? limit, SqlRowCount? offset,
        bool distinct, SqlExpressionEvaluator evaluator, SqlStatementContext statement, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        int operations = 0;
        var keyed = new List<(object?[] Output, object?[] Keys)>();
        foreach (var source in matches)
        {
            var output = new object?[projections.Count];
            for (int i = 0; i < output.Length; i++)
            {
                var projection = projections[i];
                await PrepareSubqueriesAsync(projection.Value, source, statement, cancellationToken).ConfigureAwait(false);
                output[i] = projection.ColumnOrdinal is int ordinal ? SqlBinaryValue.CopyShared(source[ordinal])
                    : NormalizeGroupValue(evaluator.Evaluate(projection.Value!, source, copySharedBinary: true), projection.Type);
                if (output[i] is byte[])
                {
                    for (int previous = 0; previous < i; previous++)
                    {
                        if (ReferenceEquals(output[previous], output[i]))
                        {
                            output[i] = SqlBinaryValue.CopyShared(output[i]);
                            break;
                        }
                    }
                }
            }
            var orderedRow = new object?[source.Length + output.Length];
            source.CopyTo(orderedRow, 0);
            output.CopyTo(orderedRow, source.Length);
            var keys = new object?[orderBy.Count];
            for (int i = 0; i < keys.Length; i++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                await PrepareSubqueriesAsync(orderBy[i].Key, orderedRow, statement, cancellationToken).ConfigureAwait(false);
                keys[i] = evaluator.Evaluate(orderBy[i].Key, orderedRow);
            }
            keyed.Add((output, keys));
        }
        IOrderedEnumerable<(object?[] Output, object?[] Keys)>? sorted = null;
        for (int i = 0; i < orderBy.Count; i++)
        {
            int key = i;
            var ordering = orderBy[i];
            var collation = ordering.Collation.Resolve(_subqueryValues);
            int direction = ordering.IsDescending ? -1 : 1;
            int nullComparison = ordering.NullsFirst ? -1 : 1;
            var comparer = Comparer<object?>.Create((left, right) =>
            {
                if ((operations++ & 31) == 0) { cancellationToken.ThrowIfCancellationRequested(); }
                return CompareOrderingValues(left, right, direction, nullComparison, collation);
            });
            sorted = sorted is null
                ? keyed.OrderBy(item => item.Keys[key], comparer)
                : sorted.ThenBy(item => item.Keys[key], comparer);
        }
        List<object?[]> projected;
        try { projected = (sorted is null ? keyed : sorted.AsEnumerable()).Select(item => item.Output).ToList(); }
        catch (InvalidOperationException exception) when (exception.InnerException is DatabaseException or OperationCanceledException)
        {
            ExceptionDispatchInfo.Throw(exception.InnerException);
            throw;
        }
        cancellationToken.ThrowIfCancellationRequested();
        if (distinct) { projected = Deduplicate(projected, projections, evaluator); }
        var columns = projections.Select((projection, ordinal) => new DatabaseColumn
        {
            Name = projection.Name, Ordinal = ordinal, Type = projection.Type,
            MaxLength = projection.MaxLength, IsNullable = projection.IsNullable,
        }).ToArray();
        return new SqlMaterializedResultSet(columns, ApplyRowWindow(projected, limit, offset));
    }
}

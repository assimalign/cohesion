using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

using Assimalign.Cohesion.Database.Execution;
using Assimalign.Cohesion.Database.Sql.Language;

namespace Assimalign.Cohesion.Database.Sql.Internal;

internal sealed partial class SqlPlanExecutor
{
    private async Task<DatabaseResult> ExecuteSetOperationAsync(SqlSetOperationPlan plan,
        SqlStatementContext statement, CancellationToken cancellationToken)
    {
        System.Runtime.CompilerServices.RuntimeHelpers.EnsureSufficientExecutionStack();
        using var recursiveScratch = RecursiveState is null ? null : new SqlWindowMemory(statement.Metrics,
            _definitions.StatementMemoryBudget, statementBudgetOnly: true);
        var evaluator = ExecutionEvaluator(_subqueryValues, cancellationToken);
        var comparer = new GroupKeyComparer(plan.Projections.Select(projection => projection.Collation.Resolve(_subqueryValues)).ToArray());
        var rows = new List<object?[]>();
        if (plan.Operator == SqlSetOperator.Union)
        {
            var seen = plan.IsAll ? null : new HashSet<object?[]>(comparer);
            foreach (var operand in plan.Operands)
            {
                await MaterializeSetOperandAsync(operand.Query, operand.Values, plan, statement, cancellationToken, rows, seen).ConfigureAwait(false);
            }
        }
        else
        {
            var left = await MaterializeSetOperandAsync(plan.Operands[0].Query, plan.Operands[0].Values, plan, statement, cancellationToken).ConfigureAwait(false);
            var right = await MaterializeSetOperandAsync(plan.Operands[1].Query, plan.Operands[1].Values, plan, statement, cancellationToken).ConfigureAwait(false);
            var counts = new Dictionary<object?[], int>(comparer);
            foreach (var row in right)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (!counts.TryGetValue(row, out int count)) { recursiveScratch?.Reserve(64); }
                counts[row] = count + 1;
            }
            var seen = plan.IsAll ? null : new HashSet<object?[]>(comparer);
            // PostgreSQL src/backend/executor/nodeSetOp.c:128-160: INTERSECT ALL
            // emits min(m,n); EXCEPT ALL emits max(m-n,0). NULL keys compare equal.
            foreach (var row in left)
            {
                cancellationToken.ThrowIfCancellationRequested();
                counts.TryGetValue(row, out int count);
                if (plan.IsAll && count > 0) { counts[row] = count - 1; }
                bool include = plan.Operator == SqlSetOperator.Intersect ? count > 0 : count == 0;
                if (include && (seen is null || seen.Add(row))) { recursiveScratch?.Reserve(64); rows.Add(row); }
            }
        }
        return MaterializeSelect(rows, plan.Projections, plan.OrderBy, plan.Limit, plan.Offset, false, evaluator, null, cancellationToken);
    }

    private async Task<List<object?[]>> MaterializeSetOperandAsync(SqlPlan operand,
        IReadOnlyList<SqlBoundExpression> conversions, SqlSetOperationPlan plan,
        SqlStatementContext statement, CancellationToken cancellationToken,
        List<object?[]>? destination = null, HashSet<object?[]>? seen = null)
    {
        System.Runtime.CompilerServices.RuntimeHelpers.EnsureSufficientExecutionStack();
        var parent = _subqueryValues;
        _subqueryValues = parent is null ? null : new SqlSubqueryValues(parent);
        try
        {
            await using var result = (DatabaseResultSet)await ExecuteAsync(operand, statement, cancellationToken).ConfigureAwait(false);
            var evaluator = ExecutionEvaluator(_subqueryValues, cancellationToken);
            var rows = destination ?? new List<object?[]>();
            await foreach (var row in result.GetRowsAsync(cancellationToken).ConfigureAwait(false))
            {
                cancellationToken.ThrowIfCancellationRequested();
                var values = new object?[row.FieldCount];
                for (int i = 0; i < values.Length; i++)
                {
                    values[i] = SqlBinaryValue.CopyShared(SqlCharacter.Input(row.GetValue(i), result.Columns[i].Type));
                }
                var converted = new object?[values.Length];
                for (int i = 0; i < values.Length; i++)
                {
                    converted[i] = SqlTemporalArithmetic.Promote(evaluator.Evaluate(conversions[i], values), plan.Projections[i].Type);
                }
                if (seen is null || seen.Add(converted))
                {
                    if (RecursiveState is not null && SqlWindowMemory.For(statement.Metrics) is { } memory)
                    { memory.Retain(converted); memory.Reserve(32); }
                    rows.Add(converted);
                }
            }
            return rows;
        }
        finally { _subqueryValues = parent; }
    }
}

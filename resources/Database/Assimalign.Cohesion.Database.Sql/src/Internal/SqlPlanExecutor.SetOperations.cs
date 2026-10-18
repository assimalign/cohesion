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
        var left = await MaterializeSetOperandAsync(plan.Left, plan.LeftValues, plan, statement, cancellationToken).ConfigureAwait(false);
        var right = await MaterializeSetOperandAsync(plan.Right, plan.RightValues, plan, statement, cancellationToken).ConfigureAwait(false);
        var evaluator = ExecutionEvaluator(_subqueryValues, cancellationToken);
        var comparer = new GroupKeyComparer(plan.Projections.Select(projection => projection.Collation.Resolve(_subqueryValues)).ToArray());
        var rows = new List<object?[]>();
        if (plan.Operator == SqlSetOperator.Union)
        {
            var seen = plan.IsAll ? null : new HashSet<object?[]>(comparer);
            foreach (var row in left.Concat(right))
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (seen is null || seen.Add(row)) { rows.Add(row); }
            }
        }
        else
        {
            var counts = new Dictionary<object?[], int>(comparer);
            foreach (var row in right)
            {
                cancellationToken.ThrowIfCancellationRequested();
                counts.TryGetValue(row, out int count);
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
                if (include && (seen is null || seen.Add(row))) { rows.Add(row); }
            }
        }
        return MaterializeSelect(rows, plan.Projections, plan.OrderBy, plan.Limit, plan.Offset, false, evaluator, null);
    }

    private async Task<List<object?[]>> MaterializeSetOperandAsync(SqlPlan operand,
        IReadOnlyList<SqlBoundExpression> conversions, SqlSetOperationPlan plan,
        SqlStatementContext statement, CancellationToken cancellationToken)
    {
        var parent = _subqueryValues;
        _subqueryValues = parent is null ? null : new SqlSubqueryValues(parent);
        try
        {
            await using var result = (DatabaseResultSet)await ExecuteAsync(operand, statement, cancellationToken).ConfigureAwait(false);
            var evaluator = ExecutionEvaluator(_subqueryValues, cancellationToken);
            var rows = new List<object?[]>();
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
                rows.Add(converted);
            }
            return rows;
        }
        finally { _subqueryValues = parent; }
    }
}

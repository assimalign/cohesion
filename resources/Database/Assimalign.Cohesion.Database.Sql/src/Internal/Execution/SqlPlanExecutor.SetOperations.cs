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
        SqlSelectMemory retainedBytes = default, seenBytes = default, countsBytes = default;
        long operandBytes = 0;
        try
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
                    int start = rows.Count;
                    await MaterializeSetOperandAsync(operand.Query, operand.Values, plan, statement, cancellationToken, rows, seen).ConfigureAwait(false);
                    for (int i = start; i < rows.Count; i++)
                    { RetainSelectRow(ref retainedBytes, rows[i], includeWindow: true); if (seen is not null) { RetainSelectRow(ref seenBytes, rows[i], includeWindow: true, overhead: 64); } }
                }
            }
            else
            {
                var left = await MaterializeSetOperandAsync(plan.Operands[0].Query, plan.Operands[0].Values, plan, statement, cancellationToken).ConfigureAwait(false);
                operandBytes += AdmitSelectRows(left, cancellationToken, includeWindow: true);
                var right = await MaterializeSetOperandAsync(plan.Operands[1].Query, plan.Operands[1].Values, plan, statement, cancellationToken).ConfigureAwait(false);
                operandBytes += AdmitSelectRows(right, cancellationToken, includeWindow: true);
                var counts = new Dictionary<object?[], int>(comparer);
                foreach (var row in right)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    if (!counts.TryGetValue(row, out int count))
                    { recursiveScratch?.Reserve(64); RetainSelectRow(ref countsBytes, row, includeWindow: true, overhead: 64); }
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
                    if (include && (seen is null || !seen.Contains(row)))
                    {
                        if (seen is not null) { RetainSelectRow(ref seenBytes, row, includeWindow: true, overhead: 64); seen.Add(row); }
                        recursiveScratch?.Reserve(64); RetainSelectRow(ref retainedBytes, row, includeWindow: true); rows.Add(row);
                    }
                }
            }
            return MaterializeSelect(rows, plan.Projections, plan.OrderBy, plan.Limit, plan.Offset, false, evaluator, null, cancellationToken);
        }
        finally { ReleaseSelectMemory(ref retainedBytes); ReleaseSelectMemory(ref seenBytes); ReleaseSelectMemory(ref countsBytes); ReleaseSelectMemory(ref operandBytes); }
    }

    private async Task<List<object?[]>> MaterializeSetOperandAsync(SqlPlan operand,
        IReadOnlyList<SqlBoundExpression> conversions, SqlSetOperationPlan plan,
        SqlStatementContext statement, CancellationToken cancellationToken,
        List<object?[]>? destination = null, HashSet<object?[]>? seen = null)
    {
        long resultBytes = 0;
        SqlSelectMemory retainedBytes = default, seenBytes = default;
        try
        {
            System.Runtime.CompilerServices.RuntimeHelpers.EnsureSufficientExecutionStack();
            var parent = _subqueryValues;
            _subqueryValues = parent is null ? null : new SqlSubqueryValues(parent);
            try
            {
                await using var result = (DatabaseResultSet)await ExecuteAsync(operand, statement, cancellationToken).ConfigureAwait(false);
                resultBytes = AdmitSelectResult(result, cancellationToken);
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
                    if (seen is null || !seen.Contains(converted))
                    {
                        if (RecursiveState is not null && SqlWindowMemory.For(statement.Metrics) is { } memory)
                        { memory.Retain(converted); memory.Reserve(32); }
                        if (seen is not null) { RetainSelectRow(ref seenBytes, converted, includeWindow: true, overhead: 64); seen.Add(converted); }
                        RetainSelectRow(ref retainedBytes, converted, includeWindow: true);
                        rows.Add(converted);
                    }
                }
                return rows;
            }
            finally { _subqueryValues = parent; }
        }
        finally { ReleaseSelectMemory(ref retainedBytes); ReleaseSelectMemory(ref seenBytes); ReleaseSelectMemory(ref resultBytes); }
    }
}

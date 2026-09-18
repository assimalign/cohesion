using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

using Assimalign.Cohesion.Database.Execution;
using Assimalign.Cohesion.Database.Indexing;
using Assimalign.Cohesion.Database.Transactions;

namespace Assimalign.Cohesion.Database.Sql.Internal;

internal sealed partial class SqlPlanExecutor
{
    /// <summary>
    /// Executes a correlated index join or a buffered nested loop. Both inputs
    /// use the same captured statement snapshot, including every index cursor.
    /// Table intent locks protect definitions; MVCC readers need no FK row locks.
    /// </summary>
    private async Task<QueryResult> ExecuteJoinAsync(SqlJoinPlan plan, SqlStatementContext statement, CancellationToken cancellationToken)
    {
        foreach (var binding in plan.Bindings.OrderBy(binding => binding.Table.ObjectId))
        {
            await statement.Coordinator.LockManager.AcquireAsync(statement.Transaction.Sequence,
                LockResource.Object(binding.Table.ObjectId), LockMode.IntentShared, cancellationToken).ConfigureAwait(false);
            EnsureCurrentDefinition(binding.Table);
        }

        var evaluator = new SqlExpressionEvaluator(plan.Columns, _parameters, plan.Bindings, defaultCollation: _catalog.DefaultCollation);
        var matches = new List<object?[]>();
        foreach (var row in EnumerateJoinRows(plan, statement, cancellationToken))
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (evaluator.Matches(plan.Condition, row) && evaluator.Matches(plan.Where, row))
            {
                matches.Add(row);
            }
        }

        return MaterializeSelect(matches, plan.Projections, plan.OrderBy, plan.Limit, plan.Offset,
            plan.IsDistinct, evaluator);
    }

    /// <summary>
    /// Produces candidates in logical column order regardless of loop direction.
    /// Without a usable index the inner input is read once, costing O(L + R)
    /// stored reads and O(L * R) predicate evaluations, with O(R) buffered rows.
    /// </summary>
    private IEnumerable<object?[]> EnumerateJoinRows(SqlJoinPlan plan, SqlStatementContext statement, CancellationToken cancellationToken)
    {
        var access = plan.Access;
        IIndex? index = null;
        if (access is not null && !_indexManager.TryGetIndex(plan.Bindings[access.InnerBinding].Table.ObjectId, access.Index.Name, out index))
        {
            access = null; // A detached index changes cost, never the result.
        }

        int inner = access?.InnerBinding ?? 1;
        var outerBinding = plan.Bindings[1 - inner];
        var innerBinding = plan.Bindings[inner];
        statement.Metrics.AccessPath = access is null ? "join-scan" : $"join-seek:{access.Index.Name}";
        List<object?[]>? buffered = null;

        foreach (var (_, outer) in Scan(outerBinding.Table, statement, cancellationToken))
        {
            IEnumerable<object?[]> candidates;
            if (access is null)
            {
                buffered ??= Scan(innerBinding.Table, statement, cancellationToken).Select(row => row.Values).ToList();
                candidates = buffered;
            }
            else
            {
                var values = JoinProbeValues(access, innerBinding, outer);
                if (values is null)
                {
                    continue;
                }
                candidates = SeekRows(innerBinding.Table, new SqlIndexSeekPath(access.Index, values, null, null),
                    index!, statement, cancellationToken).Select(row => row.Values);
            }

            foreach (var candidate in candidates)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var joined = new object?[plan.Columns.Count];
                outer.CopyTo(joined, outerBinding.Offset);
                candidate.CopyTo(joined, innerBinding.Offset);
                yield return joined;
            }
        }
    }

    /// <summary>
    /// Converts exact numeric keys to the indexed storage type. A NULL, rounded
    /// or out-of-range value cannot satisfy the mandatory equality and has no
    /// matches; all other comparisons still run against the original joined row.
    /// </summary>
    private static object?[]? JoinProbeValues(SqlJoinIndexPath access, SqlTableBinding inner, object?[] outer)
    {
        var values = new object?[access.OuterOrdinals.Count];
        for (int i = 0; i < values.Length; i++)
        {
            object? original = outer[access.OuterOrdinals[i]];
            if (original is null)
            {
                return null;
            }

            var column = inner.Table.Columns[FindColumnOrdinal(inner.Table, access.Index.ColumnNames[i])];
            try
            {
                values[i] = CoerceForColumn(original, column);
            }
            catch (DatabaseException)
            {
                return null;
            }

            if (SqlExpressionEvaluator.Compare(original, values[i]!) != 0)
            {
                return null;
            }
        }
        return values;
    }
}

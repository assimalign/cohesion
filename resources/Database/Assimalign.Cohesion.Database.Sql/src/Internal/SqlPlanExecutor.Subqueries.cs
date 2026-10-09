using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

using Assimalign.Cohesion.Database.Execution;

namespace Assimalign.Cohesion.Database.Sql.Internal;

internal sealed partial class SqlPlanExecutor
{
    /// <summary>
    /// Runs child plans with the exact outer statement context, then stores their
    /// materialized values in their slots before executing the enclosing relation plan.
    /// No child session, transaction, or read view is opened.
    /// </summary>
    private async Task<QueryResult> ExecuteSubqueryAsync(SqlSubqueryPlan plan, SqlStatementContext statement,
        CancellationToken cancellationToken)
    {
        var values = new object?[plan.Queries.Count][];
        for (int index = 0; index < values.Length; index++)
        {
            var query = plan.Queries[index];
            cancellationToken.ThrowIfCancellationRequested();
            await using var result = (QueryResultSet)await ExecuteAsync(query.Query, statement, cancellationToken).ConfigureAwait(false);
            var rows = new List<object?>();
            bool found = false;
            await foreach (var row in result.GetRowsAsync(cancellationToken).ConfigureAwait(false))
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (query.Kind == SqlSubqueryKind.Scalar && found)
                {
                    throw new DatabaseException("Scalar subquery returned more than one row; at most one row is allowed.");
                }
                found = true;
                if (query.Kind == SqlSubqueryKind.Exists)
                {
                    break;
                }
                rows.Add(row.GetValue(0));
            }
            if (query.Kind == SqlSubqueryKind.Exists)
            {
                rows.Add(query.IsNegated ? !found : found);
            }
            else if (query.Kind == SqlSubqueryKind.Scalar && !found)
            {
                rows.Add(null); // an empty scalar subquery is a typed NULL
            }
            values[index] = rows.ToArray();
        }

        // The plan's bound expressions read these slots; nothing is substituted into a tree.
        // Rebuilding the trees instead would mean reconstructing the language package's AST
        // nodes from here, which needs its internal constructors and silently breaks whenever
        // a new plan node appears. Each level of nesting fills its own slots, so an enclosing
        // level's values stay visible while a nested plan runs.
        var subqueries = _subqueryValues ??= new SqlSubqueryValues();
        for (int index = 0; index < values.Length; index++)
        {
            subqueries.Set(plan.Queries[index].Slot, values[index]);
        }
        try
        {
            return await ExecuteAsync(plan.Input, statement, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            foreach (var query in plan.Queries)
            {
                subqueries.Clear(query.Slot);
            }
        }
    }
}

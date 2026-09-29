using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

using Assimalign.Cohesion.Database.Execution;
using Assimalign.Cohesion.Database.Sql.Language;

namespace Assimalign.Cohesion.Database.Sql.Internal;

internal sealed partial class SqlPlanExecutor
{
    /// <summary>
    /// Runs child plans with the exact outer statement context, then substitutes
    /// their materialized values before executing the enclosing relation plan.
    /// No child session, transaction, or read view is opened.
    /// </summary>
    private async Task<QueryResult> ExecuteSubqueryAsync(SqlSubqueryPlan plan, SqlStatementContext statement,
        CancellationToken cancellationToken)
    {
        var values = new Dictionary<SqlExpression, SqlExpression[]>();
        foreach (var query in plan.Queries)
        {
            cancellationToken.ThrowIfCancellationRequested();
            await using var result = (QueryResultSet)await ExecuteAsync(query.Query, statement, cancellationToken).ConfigureAwait(false);
            var rows = new List<SqlExpression>();
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
                rows.Add(new SqlConstantExpression(row.GetValue(0), query.Type, query.Collation));
            }
            if (query.Kind == SqlSubqueryKind.Exists)
            {
                rows.Add(new SqlConstantExpression(query.IsNegated ? !found : found, query.Type));
            }
            else if (query.Kind == SqlSubqueryKind.Scalar && !found)
            {
                rows.Add(new SqlConstantExpression(null, query.Type, query.Collation));
            }
            values.Add(query.Source, rows.ToArray());
        }

        // The slots stay in the plan's expression trees; every evaluator built while the
        // input plan runs resolves them against these values. Rebuilding the trees instead
        // would mean reconstructing the language package's AST nodes from here, which needs
        // its internal constructors and silently breaks whenever a new plan node appears.
        var outer = _subqueryValues;
        _subqueryValues = Merge(outer, values);
        try
        {
            return await ExecuteAsync(plan.Input, statement, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _subqueryValues = outer;
        }

        static IReadOnlyDictionary<SqlExpression, SqlExpression[]> Merge(
            IReadOnlyDictionary<SqlExpression, SqlExpression[]>? outer,
            Dictionary<SqlExpression, SqlExpression[]> inner)
        {
            if (outer is null || outer.Count == 0)
            {
                return inner;
            }

            // A nested subquery plan runs inside an enclosing one, and each level's slots
            // are distinct instances, so both levels' values stay visible to the evaluator.
            var merged = new Dictionary<SqlExpression, SqlExpression[]>(inner);
            foreach (var pair in outer)
            {
                merged[pair.Key] = pair.Value;
            }
            return merged;
        }
    }
}

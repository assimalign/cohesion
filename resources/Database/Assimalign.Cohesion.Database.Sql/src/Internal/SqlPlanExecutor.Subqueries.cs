using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

using Assimalign.Cohesion.Database.Execution;

namespace Assimalign.Cohesion.Database.Sql.Internal;

internal sealed partial class SqlPlanExecutor
{
    /// <summary>Runs independent children once and registers correlated children in this execution's frame.</summary>
    private async Task<DatabaseResult> ExecuteSubqueriesAsync(SqlPlan input, IReadOnlyList<SqlSubqueryBinding> queries,
        SqlStatementContext statement, CancellationToken cancellationToken)
    {
        var parent = _subqueryValues;
        var frame = new SqlSubqueryValues(parent, queries[0].ScopeId, queries);
        _subqueryValues = frame;
        try
        {
            foreach (var query in queries)
            {
                if (query.IsCorrelated) { continue; }
                if (!frame.StatementValues.TryGetValue(query.Slot, out var values))
                {
                    values = await MaterializeSubqueryAsync(query, statement, cancellationToken).ConfigureAwait(false);
                    frame.StatementValues.Add(query.Slot, values);
                }
                frame.Set(query.Slot, values);
            }
            return await ExecuteAsync(input, statement, cancellationToken).ConfigureAwait(false);
        }
        finally { _subqueryValues = parent; }
    }

    private async Task<object?[]> MaterializeSubqueryAsync(SqlSubqueryBinding query, SqlStatementContext statement,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var parent = _subqueryValues;
        // A child without subqueries still has its own execution frame, so its row
        // evaluation cannot replace the row retained by the enclosing frame.
        _subqueryValues = new SqlSubqueryValues(parent);
        try
        {
            await using var result = (DatabaseResultSet)await ExecuteAsync(query.Query, statement, cancellationToken).ConfigureAwait(false);
            var rows = new List<object?>();
            bool found = false;
            await foreach (var row in result.GetRowsAsync(cancellationToken).ConfigureAwait(false))
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (query.Kind == SqlSubqueryKind.Scalar && found)
                {
                    throw new DatabaseException(ErrorMessages.ScalarSubqueryCardinality);
                }
                found = true;
                if (query.Kind == SqlSubqueryKind.Exists) { break; }
                rows.Add(SqlCharacter.Input(row.GetValue(0), result.Columns[0].Type));
            }
            if (query.Kind == SqlSubqueryKind.Exists) { rows.Add(query.IsNegated ? !found : found); }
            else if (query.Kind == SqlSubqueryKind.Scalar && !found) { rows.Add(null); }
            return rows.ToArray();
        }
        finally { _subqueryValues = parent; }
    }
}

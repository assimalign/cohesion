using System;
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
        bool deferred = false;
        for (int index = 0; index < queries.Count; index++) { deferred |= queries[index].IsDeferred; }
        var frame = new SqlSubqueryValues(parent, queries[0].ScopeId, queries, lazy: deferred);
        _subqueryValues = frame;
        try
        {
            foreach (var query in queries)
            {
                if (query.IsCorrelated || query.IsDeferred) { continue; }
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

    private Task<object?[]> MaterializeSubqueryAsync(SqlSubqueryBinding query, SqlStatementContext statement, CancellationToken cancellationToken)
        => SqlWindowMemory.For(statement.Metrics) is { } memory
            ? MaterializeWindowSubqueryAsync(query, statement, cancellationToken, memory)
            : MaterializePlainSubqueryAsync(query, statement, cancellationToken);

    private async Task<object?[]> MaterializePlainSubqueryAsync(SqlSubqueryBinding query, SqlStatementContext statement,
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
                    throw new SqlExecutionException(SqlExecutionException.CardinalityCode, ErrorMessages.ScalarSubqueryCardinality);
                }
                found = true;
                if (query.Kind == SqlSubqueryKind.Exists) { break; }
                rows.Add(SqlCharacter.Input(row.GetValue(0), result.Columns[0].Type));
            }
            if (query.Kind == SqlSubqueryKind.Exists) { rows.Add(query.IsNegated ? !found : found); }
            else if (query.Kind == SqlSubqueryKind.Scalar && !found) { rows.Add(null); }
            // Completed async frames can retain a disposed child, especially when
            // EXISTS stopped enumeration early. Only the copied values belong to
            // the statement cache; drop the child's owned source cells and buffer
            // before a subsequent window acquires its grant.
            if (result is SqlMaterializedResultSet materialized)
            {
                for (int index = 0; index < materialized.Rows.Count; index++)
                { Array.Clear(materialized.Rows[index]); }
                materialized.ClearRows();
            }
            return rows.ToArray();
        }
        finally { _subqueryValues = parent; }
    }

    // This separate state machine charges child result buffers only inside a window.
    private async Task<object?[]> MaterializeWindowSubqueryAsync(SqlSubqueryBinding query, SqlStatementContext statement,
        CancellationToken cancellationToken, SqlWindowMemory memory)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var parent = _subqueryValues;
        // A child without subqueries still has its own execution frame, so its row
        // evaluation cannot replace the row retained by the enclosing frame.
        _subqueryValues = new SqlSubqueryValues(parent);
        try
        {
            await using var result = (DatabaseResultSet)await ExecuteAsync(query.Query, statement, cancellationToken).ConfigureAwait(false);
            // An inner window transfers its result after releasing its own stage grant.
            // Adopt those rows immediately; a plain child already shares this owner.
            if (result is SqlMaterializedResultSet materialized)
            { foreach (var source in materialized.Rows) { memory.Retain(source); } }
            var rows = new List<object?>();
            long bufferBytes = 0;
            try
            {
                bool found = false;
                await foreach (var row in result.GetRowsAsync(cancellationToken).ConfigureAwait(false))
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    if (query.Kind == SqlSubqueryKind.Scalar && found)
                    {
                        throw new SqlExecutionException(SqlExecutionException.CardinalityCode, ErrorMessages.ScalarSubqueryCardinality);
                    }
                    found = true;
                    if (query.Kind == SqlSubqueryKind.Exists) { break; }
                    Add(SqlCharacter.Input(row.GetValue(0), result.Columns[0].Type));
                }
                if (query.Kind == SqlSubqueryKind.Exists) { Add(query.IsNegated ? !found : found); }
                else if (query.Kind == SqlSubqueryKind.Scalar && !found) { Add(null); }
                var values = rows.ToArray();
                memory.RetainResult(values, transient: query.IsCorrelated);
                return values;

                void Add(object? value)
                {
                    if (rows.Count == rows.Capacity)
                    {
                        int capacity = rows.Capacity == 0 ? 4 : checked(rows.Capacity * 2);
                        long next = 24L + 8L * capacity;
                        memory.Reserve(next);
                        rows.Capacity = capacity;
                        memory.Release(bufferBytes);
                        bufferBytes = next;
                    }
                    if (value is not null) { memory.RetainValue(value); }
                    rows.Add(value);
                }
            }
            finally
            {
                foreach (var value in rows) { if (value is not null) { memory.ReleaseValue(value); } }
                memory.Release(bufferBytes);
            }
        }
        finally { _subqueryValues = parent; }
    }
}

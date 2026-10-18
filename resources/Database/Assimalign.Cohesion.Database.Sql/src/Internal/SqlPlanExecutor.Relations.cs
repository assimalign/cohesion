using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

using Assimalign.Cohesion.Database.Execution;

namespace Assimalign.Cohesion.Database.Sql.Internal;

internal sealed partial class SqlPlanExecutor
{
    private Dictionary<SqlDerivedRelation, List<object?[]>>? _materializedRelations;

    private async Task<List<object?[]>> MaterializeRelationAsync(SqlDerivedRelation relation,
        SqlStatementContext statement, CancellationToken cancellationToken)
    {
        if (!relation.IsCorrelated && _materializedRelations?.TryGetValue(relation, out var cached) == true) { return cached; }
        var parent = _subqueryValues;
        _subqueryValues = parent is null ? null : new SqlSubqueryValues(parent);
        try
        {
            await using var result = (DatabaseResultSet)await ExecuteAsync(relation.Query, statement, cancellationToken).ConfigureAwait(false);
            var rows = new List<object?[]>();
            await foreach (var row in result.GetRowsAsync(cancellationToken).ConfigureAwait(false))
            {
                cancellationToken.ThrowIfCancellationRequested();
                var values = new object?[row.FieldCount];
                for (int i = 0; i < values.Length; i++)
                {
                    values[i] = SqlBinaryValue.CopyShared(SqlCharacter.Input(row.GetValue(i), result.Columns[i].Type));
                }
                rows.Add(values);
            }
            if (!relation.IsCorrelated) { (_materializedRelations ??= new(ReferenceEqualityComparer.Instance)).Add(relation, rows); }
            return rows;
        }
        finally { _subqueryValues = parent; }
    }
}

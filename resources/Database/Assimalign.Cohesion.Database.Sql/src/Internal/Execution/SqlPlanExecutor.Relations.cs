using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

using Assimalign.Cohesion.Database.Execution;

namespace Assimalign.Cohesion.Database.Sql.Internal;

internal sealed partial class SqlPlanExecutor
{
    private RelationRows? _materializedRelations;
    private DateTimeOffset? ViewTransactionStart => _materializedRelations?.TransactionStart;

    private async Task<List<object?[]>> MaterializeRelationAsync(SqlDerivedRelation relation,
        SqlStatementContext statement, CancellationToken cancellationToken)
    {
        if (RecursiveState is { } recursive)
        {
            var recursiveRows = new List<object?[]>();
            await foreach (var row in ReadRecursiveRelationAsync(relation, _subqueryValues, statement, cancellationToken).ConfigureAwait(false))
            { recursive.Memory.Retain(row); recursive.Memory.Reserve(32); recursiveRows.Add(row); }
            return recursiveRows;
        }
        if (!relation.IsCorrelated && _materializedRelations?.TryGetValue(relation, out var cached) == true) { return cached; }
        var parent = _subqueryValues;
        var previousRelations = _materializedRelations;
        if (relation.CatalogView is not null) { _materializedRelations = new RelationRows(statement.TransactionStart); }
        _subqueryValues = relation.CatalogView is not null ? null : parent is null ? null : new SqlSubqueryValues(parent);
        var rows = new List<object?[]>();
        long retainedBytes = 0, resultBytes = 0;
        try
        {
            using var analysis = statement.Metrics.EnterRelationAnalysis(relation);
            await using var result = (DatabaseResultSet)await ExecuteAsync(relation.Query, statement, cancellationToken).ConfigureAwait(false);
            resultBytes = AdmitSelectResult(result, cancellationToken);
            await foreach (var row in result.GetRowsAsync(cancellationToken).ConfigureAwait(false))
            {
                cancellationToken.ThrowIfCancellationRequested();
                var values = new object?[row.FieldCount];
                for (int i = 0; i < values.Length; i++)
                { values[i] = SqlBinaryValue.CopyShared(SqlCharacter.Input(row.GetValue(i), result.Columns[i].Type)); }
                if (statement.Metrics.DmlMemory(_definitions.StatementMemoryBudget) is { } memory)
                {
                    memory.Retain(values);
                    memory.Reserve(32);
                }
                RetainSelectRow(ref retainedBytes, values, includeWindow: true);
                rows.Add(values);
            }
            if (!relation.IsCorrelated)
            {
                // A view's nested cache is scoped to its body, whereas these copied rows
                // enter the enclosing statement cache after catalog scope restoration.
                var destination = relation.CatalogView is not null ? previousRelations : _materializedRelations;
                destination ??= new RelationRows();
                destination.Add(relation, rows);
                destination.Reserved += retainedBytes;
                retainedBytes = 0;
                if (relation.CatalogView is not null) { previousRelations = destination; }
                else { _materializedRelations = destination; }
            }
        }
        finally
        {
            ReleaseSelectMemory(ref resultBytes);
            ReleaseSelectMemory(ref retainedBytes);
            if (relation.CatalogView is not null && _materializedRelations is { } scoped)
            { ReleaseSelectMemory(ref scoped.Reserved); }
            _subqueryValues = parent;
            if (relation.CatalogView is not null) { _materializedRelations = previousRelations; }
        }
        return rows;
    }

    // Optional relation state keeps ordinary stored-table execution allocation-neutral.
    private class RelationRows : Dictionary<SqlDerivedRelation, List<object?[]>>
    {
        internal RelationRows(DateTimeOffset? transactionStart = null) : base(ReferenceEqualityComparer.Instance)
        { TransactionStart = transactionStart; }
        internal DateTimeOffset? TransactionStart { get; }
        internal long Reserved;
    }
}

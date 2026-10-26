using System.Collections.Generic;
using System.Linq;

using Assimalign.Cohesion.Database.Sql.Catalog;
using Assimalign.Cohesion.Database.Sql.Language;

namespace Assimalign.Cohesion.Database.Sql.Internal;

internal sealed partial class SqlPlanner
{
    private IReadOnlyList<SqlProjection>? BindReturning(IReadOnlyList<SqlSelectColumn>? returning,
        SqlCatalogTable table, SqlTableReference target)
    {
        if (returning is null) { return null; }
        foreach (var column in returning)
        {
            if (ContainsAggregate(column.Expression))
            {
                throw new SqlPlanningException("CS05017", ErrorMessages.ReturningAggregate, column.Expression.Location);
            }
        }
        // INSERT SELECT's input namespace is deliberately absent here, as in PostgreSQL
        // src/backend/parser/analyze.c:1035-1044. Only the written target row is visible.
        // Even immutable calls run per written row, after that row's index checks inside apply.
        var evaluator = new SqlExpressionEvaluator(table.Columns, _parameters,
            defaultCollation: _catalog.DefaultCollation, functions: _functions,
            cancellationToken: _cancellationToken, relation: target, foldConstants: false);
        return BindProjections(returning, table.Columns, evaluator);
    }

    private IReadOnlyList<SqlProjection>? BindSourceReturning(IReadOnlyList<SqlSelectColumn>? returning,
        SqlJoinPlan associations, SqlTableReference target)
    {
        if (returning is null) { return null; }
        foreach (var column in returning)
        {
            if (ContainsAggregate(column.Expression))
            {
                throw new SqlPlanningException("CS05017", ErrorMessages.ReturningAggregate, column.Expression.Location);
            }
        }
        var evaluator = DmlEvaluator(associations.Columns, associations.Bindings, target, foldConstants: false);
        // Cohesion keeps bare RETURNING * target-only; explicit source columns use the joined namespace.
        return BindProjections(returning, associations.Columns, evaluator,
            Enumerable.Range(0, associations.Bindings[0].Source.Columns.Count).ToArray());
    }

    private List<SqlProjection> BindProjections(IReadOnlyList<SqlSelectColumn> selectColumns,
        IReadOnlyList<SqlCatalogColumn> columns, SqlExpressionEvaluator evaluator, IReadOnlyList<int>? outputOrdinals = null)
    {
        var projections = new List<SqlProjection>(selectColumns.Count);
        foreach (var column in selectColumns)
        {
            if (column.Expression is SqlStarExpression)
            {
                for (int i = 0; i < (outputOrdinals?.Count ?? columns.Count); i++)
                {
                    int ordinal = outputOrdinals is null ? i : outputOrdinals[i];
                    projections.Add(PassThrough(columns[ordinal].Name, ordinal, columns, evaluator, merged: true));
                }
            }
            else if (column.Expression is SqlColumnReferenceExpression reference && !evaluator.IsOuterColumn(reference))
            {
                int ordinal = evaluator.ResolveColumn(reference);
                projections.Add(PassThrough(column.Alias ?? columns[ordinal].Name, ordinal, columns, evaluator,
                    merged: reference.TableAlias is null && reference.SchemaName is null));
            }
            else
            {
                ValidateExpression(column.Expression, evaluator, _subqueryTypes);
                var value = evaluator.Bind(column.Expression, out SqlBoundCollation collation, out var candidate);
                projections.Add(new SqlProjection(
                    column.Alias ?? (column.Expression is SqlColumnReferenceExpression outerColumn
                        ? outerColumn.ColumnName : $"column{projections.Count + 1}"), null, column.Expression,
                    GroupExpressionType(column.Expression, columns, evaluator)) { Value = value, Collation = collation,
                        Metadata = CaptureProjectionMetadata(candidate,
                            UnwrapCollation(column.Expression) is SqlColumnReferenceExpression projectedColumn ? evaluator.ColumnMetadata(projectedColumn) : null,
                            column.Expression, evaluator),
                        MaxLength = evaluator.MaximumLengthOf(column.Expression),
                        IsNullable = column.Expression is not SqlColumnReferenceExpression nullableColumn || evaluator.ColumnMetadata(nullableColumn).IsNullable });
            }
        }

        return projections;
    }
}

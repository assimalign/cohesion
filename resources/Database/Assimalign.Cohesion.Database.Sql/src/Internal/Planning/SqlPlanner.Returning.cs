using System;
using System.Collections.Generic;
using System.Globalization;

using Assimalign.Cohesion.Database.Language;

using Assimalign.Cohesion.Database.Sql.Catalog;
using Assimalign.Cohesion.Database.Sql.Language;
using Assimalign.Cohesion.Database.Sql.Language.Internal;

namespace Assimalign.Cohesion.Database.Sql.Internal;

internal sealed partial class SqlPlanner
{
    private static void CheckTargetList(int count, int additional, Location? location)
    {
        if (additional > SqlTargetListLimits.MaximumTargets - count)
        {
            throw new SqlPlanningException(SqlTargetListLimits.Code, string.Format(CultureInfo.CurrentCulture,
                ErrorMessages.TargetListLimit, SqlTargetListLimits.MaximumTargets, (long)count + additional), location);
        }
    }

    private static void EnsureTargetCapacity<T>(List<T> targets, int additional, Location? location)
    {
        CheckTargetList(targets.Count, additional, location);
        int required = targets.Count + additional;
        if (targets.Capacity < required)
        {
            targets.Capacity = Math.Min(SqlTargetListLimits.MaximumTargets, Math.Max(required, Math.Max(4, targets.Capacity * 2)));
        }
    }

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
        SqlDmlSource source, SqlTableReference target)
    {
        if (returning is null) { return null; }
        foreach (var column in returning)
        {
            if (ContainsAggregate(column.Expression))
            {
                throw new SqlPlanningException("CS05017", ErrorMessages.ReturningAggregate, column.Expression.Location);
            }
        }
        var associations = source.Associations;
        var evaluator = DmlEvaluator(associations.Columns, associations.Bindings, target, foldConstants: false);
        // PostgreSQL parse_target.c:1301-1321 expands the visible namespace, preserving
        // merged USING output order rather than exposing every physical association slot.
        return BindProjections(returning, associations.Columns, evaluator, source.VisibleOrdinals);
    }

    private List<SqlProjection> BindProjections(IReadOnlyList<SqlSelectColumn> selectColumns,
        IReadOnlyList<SqlCatalogColumn> columns, SqlExpressionEvaluator evaluator, IReadOnlyList<int>? outputOrdinals = null)
    {
        var projections = new List<SqlProjection>(Math.Min(selectColumns.Count, SqlTargetListLimits.MaximumTargets));
        foreach (var column in selectColumns)
        {
            if (column.Expression is SqlStarExpression)
            {
                EnsureTargetCapacity(projections, outputOrdinals?.Count ?? columns.Count, column.Expression.Location);
                for (int i = 0; i < (outputOrdinals?.Count ?? columns.Count); i++)
                {
                    int ordinal = outputOrdinals is null ? i : outputOrdinals[i];
                    projections.Add(PassThrough(columns[ordinal].Name, ordinal, columns, evaluator, merged: true));
                }
            }
            else if (column.Expression is SqlQualifiedStarExpression star)
            {
                var ordinals = evaluator.QualifiedStarOrdinals(star);
                EnsureTargetCapacity(projections, ordinals.Count, star.Location);
                foreach (int ordinal in ordinals)
                {
                    projections.Add(PassThrough(columns[ordinal].Name, ordinal, columns, evaluator, merged: false));
                }
            }
            else if (column.Expression is SqlColumnReferenceExpression reference && !evaluator.IsOuterColumn(reference))
            {
                EnsureTargetCapacity(projections, 1, column.Expression.Location);
                int ordinal = evaluator.ResolveColumn(reference);
                projections.Add(PassThrough(column.Alias ?? columns[ordinal].Name, ordinal, columns, evaluator,
                    merged: reference.TableAlias is null && reference.SchemaName is null, explicitName: column.Alias is not null));
            }
            else
            {
                EnsureTargetCapacity(projections, 1, column.Expression.Location);
                ValidateExpression(column.Expression, evaluator, _subqueryTypes);
                var value = evaluator.Bind(column.Expression, out SqlBoundCollation collation, out var candidate);
                projections.Add(new SqlProjection(
                    column.Alias ?? (column.Expression is SqlColumnReferenceExpression outerColumn
                        ? outerColumn.ColumnName : $"column{projections.Count + 1}"), null, column.Expression,
                    GroupExpressionType(column.Expression, columns, evaluator)) { Value = value, Collation = collation,
                        HasOutputName = column.Alias is not null || column.Expression is SqlColumnReferenceExpression,
                        HasSynthesizedName = column.Alias is null && column.Expression is SqlColumnReferenceExpression inheritedName &&
                            evaluator.ColumnMetadata(inheritedName).HasSynthesizedName,
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

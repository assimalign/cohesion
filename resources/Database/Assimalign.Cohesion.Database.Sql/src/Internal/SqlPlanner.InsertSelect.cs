using System;
using System.Collections.Generic;
using System.Linq;

using Assimalign.Cohesion.Database.Sql.Catalog;
using Assimalign.Cohesion.Database.Sql.Language;
using Assimalign.Cohesion.Database.Types;

namespace Assimalign.Cohesion.Database.Sql.Internal;

internal sealed partial class SqlPlanner
{
    /// <summary>
    /// Validates the source shape before execution, including queries that return
    /// no rows. Value-dependent conversions are checked by the shared insert path.
    /// </summary>
    private SqlInsertSelectPlan PlanInsertSelect(SqlCatalogTable table, IReadOnlyList<int> targetOrdinals,
        SqlSelectExpression source)
    {
        var plan = PlanSelect(source);
        var projections = InsertSourceProjections(plan);
        if (projections.Count != targetOrdinals.Count)
        {
            throw new DatabaseException(
                $"INSERT SELECT has {projections.Count} source columns but {targetOrdinals.Count} target columns.");
        }
        for (int i = 0; i < projections.Count; i++)
        {
            var target = table.Columns[targetOrdinals[i]];
            var sourceType = projections[i].Type;
            if (!CanAssignInsertType(sourceType, target.Type.Type))
            {
                throw new DatabaseException(
                    $"INSERT SELECT source column {i + 1} of type {sourceType} is incompatible with target column '{target.Name}' of type {target.Type.Type}.");
            }
        }
        return new SqlInsertSelectPlan(table, targetOrdinals, plan);
    }

    /// <summary>Reads projected types through the closed query plan family.</summary>
    private IReadOnlyList<SqlProjection> InsertSourceProjections(SqlPlan plan)
    {
        var (projections, columns, bindings) = plan switch
        {
            SqlSubqueryPlan subquery => (InsertSourceProjections(subquery.Input),
                (IReadOnlyList<SqlCatalogColumn>?)null, (IReadOnlyList<SqlTableBinding>?)null),
            SqlSelectPlan select => (select.Projections, select.Table.Columns, null),
            SqlJoinPlan join => (join.Projections, join.Columns, join.Bindings),
            SqlSystemViewPlan view => (view.Projections, view.View.Columns, null),
            SqlGroupPlan group => (group.Projections, group.SourceColumns, group.Bindings),
            _ => throw new DatabaseException("INSERT SELECT requires a query source."),
        };
        if (columns is null)
        {
            return projections;
        }
        var evaluator = new SqlExpressionEvaluator(columns, _parameters, bindings, defaultCollation: _catalog.DefaultCollation);
        return projections.Select(projection => projection.Type == DatabaseType.Null && projection.Expression is not null
            ? projection with { Type = GroupExpressionType(projection.Expression, columns, evaluator) }
            : projection).ToArray();
    }

    /// <summary>
    /// Mirrors the implicit conversion families of literal insertion. Unknown or
    /// NULL results are validated as values; incompatible declared types fail even
    /// when the source is empty.
    /// </summary>
    private static bool CanAssignInsertType(DatabaseType source, DatabaseType target)
    {
        if (source == DatabaseType.Null || source == target || target is DatabaseType.String or DatabaseType.Json)
        {
            return true;
        }
        static bool IsNumber(DatabaseType type) => type is DatabaseType.Int8 or DatabaseType.Int16
            or DatabaseType.Int32 or DatabaseType.Int64 or DatabaseType.Float32 or DatabaseType.Float64 or DatabaseType.Decimal;

        return target switch
        {
            DatabaseType.Boolean => IsNumber(source) || source is DatabaseType.String or DatabaseType.Json,
            DatabaseType.Int8 or DatabaseType.Int16 or DatabaseType.Int32 or DatabaseType.Int64
                or DatabaseType.Float32 or DatabaseType.Float64 or DatabaseType.Decimal
                => IsNumber(source) || source is DatabaseType.Boolean or DatabaseType.String or DatabaseType.Json,
            DatabaseType.Binary or DatabaseType.JsonBinary => source is DatabaseType.Binary or DatabaseType.JsonBinary,
            DatabaseType.Date => source is DatabaseType.String or DatabaseType.Json or DatabaseType.DateTime,
            DatabaseType.Time or DatabaseType.DateTime or DatabaseType.DateTimeOffset or DatabaseType.TimeSpan or DatabaseType.Guid
                => source is DatabaseType.String or DatabaseType.Json,
            _ => false,
        };
    }
}

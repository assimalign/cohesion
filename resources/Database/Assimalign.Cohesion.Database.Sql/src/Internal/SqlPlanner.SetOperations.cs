using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Runtime.CompilerServices;

using Assimalign.Cohesion.Database.Sql.Catalog;
using Assimalign.Cohesion.Database.Sql.Language;
using Assimalign.Cohesion.Database.Types;

namespace Assimalign.Cohesion.Database.Sql.Internal;

internal sealed partial class SqlPlanner
{
    private SqlPlan PlanQueryBody(SqlQueryBodyExpression query)
    {
        RuntimeHelpers.EnsureSufficientExecutionStack();
        return query is SqlSetOperationExpression set ? PlanSetOperation(set) : PlanSubqueries((SqlSelectExpression)query);
    }

    private bool IsCommonTableOperand(SqlSelectExpression select)
    {
        return _commonTableState?.BindingBody is { } body && Contains(body);

        bool Contains(SqlQueryBodyExpression query)
        {
            RuntimeHelpers.EnsureSufficientExecutionStack();
            return ReferenceEquals(query, select) || query is SqlSetOperationExpression set && (Contains(set.Left) || Contains(set.Right));
        }
    }

    private SqlSetOperationPlan PlanSetOperation(SqlSetOperationExpression set)
    {
        SqlPlan left;
        SqlPlan right;
        (_queryScopes ??= new()).SetOperandDepth++;
        try
        {
            left = PlanQuery(set.Left);
            right = PlanQuery(set.Right);
        }
        finally { _queryScopes.SetOperandDepth--; }
        var first = QueryOutput(left);
        var second = QueryOutput(right);
        if (first.Projections.Count != second.Projections.Count)
        {
            throw new SqlPlanningException("COHSQLE013", string.Format(CultureInfo.CurrentCulture,
                ErrorMessages.SetColumnCount, set.Operator, first.Projections.Count, second.Projections.Count), set.Location);
        }

        var projections = new List<SqlProjection>();
        var columns = new List<SqlCatalogColumn>();
        var leftValues = new List<SqlBoundExpression>();
        var rightValues = new List<SqlBoundExpression>();
        for (int i = 0; i < first.Projections.Count; i++)
        {
            var a = SetColumn(first.Projections[i], first.Columns);
            var b = SetColumn(second.Projections[i], second.Columns);
            // PostgreSQL parse_coerce.c:1362-1387,1455-1457: unknowns do not choose
            // a category; an all-unknown pair becomes text. Domains survive identical inputs.
            var type = a.Type.Type == DatabaseType.Null
                ? new DatabaseTypeInfo(b.Type.Type == DatabaseType.Null ? DatabaseType.String : b.Type.Type)
                : b.Type.Type == DatabaseType.Null ? new DatabaseTypeInfo(a.Type.Type)
                : UsingType(a.Type, b.Type, (i + 1).ToString(CultureInfo.InvariantCulture), set, setOperation: true);
            string? domain = a.Type.Type == b.Type.Type && a.Type.MaxLength == b.Type.MaxLength &&
                a.Type.Precision == b.Type.Precision && a.Type.Scale == b.Type.Scale && string.Equals(a.Domain, b.Domain, StringComparison.OrdinalIgnoreCase) ? a.Domain : null;
            var candidate = SqlCollationCandidate.Fold(first.Projections[i].Metadata?.Collation ?? default,
                second.Projections[i].Metadata?.Collation ?? default);
            var collation = SqlBoundCollation.Of(candidate, default, _catalog.DefaultCollation);
            bool nullable = SetNullable(left, i) || SetNullable(right, i);
            projections.Add(new SqlProjection(first.Projections[i].Name, null, null, type.Type)
            {
                Value = new SqlBoundSlot(i), Collation = collation, IsNullable = nullable,
                MaxLength = type.MaxLength, Metadata = new SqlProjectionMetadata(candidate, null, type, domain),
            });
            columns.Add(new SqlCatalogColumn(projections[i].Name, type, nullable,
                collation: candidate.Priority >= 2 ? candidate.Collation : null, domainName: domain));
            leftValues.Add(CoerceSetValue(i, a.Type.Type, type));
            rightValues.Add(CoerceSetValue(i, b.Type.Type, type));
        }
        var ordering = new List<SqlBoundOrdering>();
        foreach (var order in set.OrderBy)
        {
            int ordinal = -1;
            if (order.Expression is SqlLiteralExpression { LiteralType: SqlLiteralType.Integer } literal &&
                int.TryParse(literal.Value, NumberStyles.None, CultureInfo.InvariantCulture, out int position) &&
                position > 0 && position <= projections.Count)
            {
                ordinal = position - 1;
            }
            else if (order.Expression is SqlColumnReferenceExpression { TableAlias: null, SchemaName: null } name)
            {
                var matches = projections.Select((projection, index) => (projection, index))
                    .Where(item => string.Equals(item.projection.Name, name.ColumnName, StringComparison.OrdinalIgnoreCase)).ToArray();
                if (matches.Length == 1) { ordinal = matches[0].index; }
                else if (matches.Length > 1)
                {
                    throw new SqlPlanningException("COHSQLE019", string.Format(CultureInfo.CurrentCulture,
                        ErrorMessages.SetOrderAmbiguous, name.ColumnName), order.Expression.Location);
                }
            }
            if (ordinal < 0)
            {
                throw new SqlPlanningException("COHSQLE018", ErrorMessages.SetOrderOutput, order.Expression.Location);
            }
            ordering.Add(new SqlBoundOrdering(new SqlBoundSlot(ordinal), order.IsDescending, projections[ordinal].Collation));
        }
        return new SqlSetOperationPlan(left, right, set.Operator, set.IsAll, projections, columns,
            leftValues, rightValues, ordering, EvaluateCount(set.Limit, LimitClause(set)), EvaluateCount(set.Offset, "OFFSET"));
    }

    private (DatabaseTypeInfo Type, string? Domain) SetColumn(SqlProjection projection, IReadOnlyList<SqlCatalogColumn> columns)
    {
        if (projection.Metadata?.TypeInfo is { } setType) { return (setType, projection.Metadata.DomainName); }
        var column = projection.Metadata?.SourceColumn ?? (projection.ColumnOrdinal is int ordinal ? columns[ordinal] : null);
        if (column is not null) { return (column.Type, column.DomainName); }
        if (projection.Expression is { } expression && UnwrapCollation(expression) is SqlCastExpression cast)
        {
            var type = ResolveCastTarget(cast, _functions.Types);
            return (type.Storage, type.IsDomain ? type.Name : null);
        }
        return (new DatabaseTypeInfo(projection.Type, projection.MaxLength), null);
    }

    private SqlBoundExpression CoerceSetValue(int ordinal, DatabaseType source, DatabaseTypeInfo common)
    {
        SqlBoundExpression slot = new SqlBoundSlot(ordinal);
        if (source == common.Type || source == DatabaseType.Null || SqlTemporalArithmetic.IsCalendar(source) && SqlTemporalArithmetic.IsCalendar(common.Type))
        {
            return slot;
        }
        var target = SqlType.ForStorage(common);
        return new SqlBoundCast(slot, target.Name, target, _functions.Types.ResolveCasts(null, target),
            _functions.Database, _catalog.DefaultCollation, sourceType: source);
    }

    private static bool SetNullable(SqlPlan plan, int ordinal)
    {
        while (true)
        {
            if (plan is SqlSubqueryPlan subquery) { plan = subquery.Input; }
            else if (plan is SqlCorrelatedSubqueryPlan correlated) { plan = correlated.Input; }
            else if (plan is SqlWithPlan with) { plan = with.Input; }
            else { break; }
        }
        return plan is SqlSystemViewPlan system ? system.Projections[ordinal].Expression is SqlCastExpression
            : ((SqlRelationPlan)plan).OutputProjections[ordinal].IsNullable;
    }
}

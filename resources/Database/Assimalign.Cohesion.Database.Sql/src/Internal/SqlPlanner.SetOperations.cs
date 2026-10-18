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
        => _commonTableState?.BindingOperands?.Contains(select) == true;

    private SqlSetOperationPlan PlanSetOperation(SqlSetOperationExpression set)
    {
        var pending = new Stack<(SqlQueryBodyExpression Query, bool Visited)>();
        var plans = new Stack<SqlPlan>();
        pending.Push((set, false));
        (_queryScopes ??= new()).SetOperandDepth++;
        try
        {
            while (pending.TryPop(out var item))
            {
                _cancellationToken.ThrowIfCancellationRequested();
                if (item.Query is not SqlSetOperationExpression operation ||
                    !ReferenceEquals(item.Query, set) && item.Query.CommonTables.Count > 0)
                {
                    plans.Push(PlanQuery(item.Query));
                }
                else if (!item.Visited)
                {
                    pending.Push((operation, true));
                    pending.Push((operation.Right, false));
                    pending.Push((operation.Left, false));
                }
                else
                {
                    var right = plans.Pop();
                    var left = plans.Pop();
                    plans.Push(CombineSetPlans(operation, left, right));
                }
            }
            return (SqlSetOperationPlan)plans.Pop();
        }
        finally { _queryScopes.SetOperandDepth--; }
    }

    private SqlSetOperationPlan CombineSetPlans(SqlSetOperationExpression set, SqlPlan left, SqlPlan right)
    {
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
            var type = a.Unknown
                ? new DatabaseTypeInfo(b.Unknown ? DatabaseType.String : b.Type.Type)
                : b.Unknown ? new DatabaseTypeInfo(a.Type.Type)
                : UsingType(a.Type, b.Type, (i + 1).ToString(CultureInfo.InvariantCulture), set, setOperation: true);
            bool sameDomain = string.Equals(a.Domain, b.Domain, StringComparison.OrdinalIgnoreCase);
            if (!sameDomain) { type = new DatabaseTypeInfo(type.Type); }
            string? domain = !a.Unknown && !b.Unknown && a.Type.Type == b.Type.Type && sameDomain ? a.Domain : null;
            var candidate = type.Type is DatabaseType.String or DatabaseType.Character
                ? SqlCollationCandidate.Fold(first.Projections[i].Metadata?.Collation ?? default,
                    second.Projections[i].Metadata?.Collation ?? default) : default;
            var collation = SqlBoundCollation.Of(candidate, default, _catalog.DefaultCollation);
            bool nullable = SetNullable(left, i) || SetNullable(right, i);
            projections.Add(new SqlProjection(first.Projections[i].Name, null, null, type.Type)
            {
                Value = new SqlBoundSlot(i), Collation = collation, IsNullable = nullable,
                MaxLength = type.MaxLength, Metadata = new SqlProjectionMetadata(candidate, null, type, domain),
            });
            columns.Add(new SqlCatalogColumn(projections[i].Name, type, nullable,
                collation: candidate.Priority >= 2 ? candidate.Collation : null, domainName: domain));
            leftValues.Add(CoerceSetValue(i, a.Type.Type, type, a.Unknown ? first.Projections[i].Expression : null));
            rightValues.Add(CoerceSetValue(i, b.Type.Type, type, b.Unknown ? second.Projections[i].Expression : null));
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
        // PostgreSQL prepunion.c:1380-1420: pull compatible UNION children into
        // one Append-like node. Reuse the left list so a flat chain never copies its prefix.
        bool CanFlatten(SqlPlan child, IReadOnlyList<SqlBoundExpression> values)
            => set.Operator == SqlSetOperator.Union && child is SqlSetOperationPlan union &&
                union.Operator == set.Operator && union.IsAll == set.IsAll && union.OrderBy.Count == 0 &&
                union.Limit is null && union.Offset is null && values.All(value => value is SqlBoundSlot) &&
                union.Columns.Select(column => column.Type).SequenceEqual(columns.Select(column => column.Type)) &&
                union.Projections.Select(projection => projection.Collation).SequenceEqual(projections.Select(projection => projection.Collation));

        var operands = CanFlatten(left, leftValues) ? ((SqlSetOperationPlan)left).Operands
            : new List<SqlSetOperand> { new(left, leftValues) };
        if (CanFlatten(right, rightValues)) { operands.AddRange(((SqlSetOperationPlan)right).Operands); }
        else { operands.Add(new SqlSetOperand(right, rightValues)); }
        return new SqlSetOperationPlan(operands, set.Operator, set.IsAll, projections, columns,
            ordering, BindCount(set.Limit, LimitClause(set)), BindCount(set.Offset, "OFFSET"));
    }

    private (DatabaseTypeInfo Type, string? Domain, bool Unknown) SetColumn(SqlProjection projection, IReadOnlyList<SqlCatalogColumn> columns)
    {
        // An unadorned literal is unknown until this operand's common type is chosen
        // (PostgreSQL parse_coerce.c:1397; analyze.c:2357). CAST expressions stay typed.
        bool unknown = projection.Type == DatabaseType.Null || projection.Expression is { } value &&
            UnwrapCollation(value) is SqlLiteralExpression { LiteralType: SqlLiteralType.String };
        if (projection.Metadata?.TypeInfo is { } setType) { return (setType, projection.Metadata.DomainName, unknown); }
        var column = projection.Metadata?.SourceColumn ?? (projection.ColumnOrdinal is int ordinal ? columns[ordinal] : null);
        if (column is not null) { return (column.Type, column.DomainName, unknown); }
        if (projection.Expression is { } expression && UnwrapCollation(expression) is SqlCastExpression cast)
        {
            var type = ResolveCastTarget(cast, _functions.Types);
            return (type.Storage, type.IsDomain ? type.Name : null, false);
        }
        return (new DatabaseTypeInfo(projection.Type, projection.MaxLength), null, unknown);
    }

    private SqlBoundExpression CoerceSetValue(int ordinal, DatabaseType source, DatabaseTypeInfo common, SqlExpression? unknown)
    {
        SqlBoundExpression slot = new SqlBoundSlot(ordinal);
        if (source == common.Type || source == DatabaseType.Null || SqlTemporalArithmetic.IsCalendar(source) && SqlTemporalArithmetic.IsCalendar(common.Type))
        {
            return slot;
        }
        var target = SqlType.ForStorage(common);
        var cast = new SqlBoundCast(slot, target.Name, target, _functions.Types.ResolveCasts(null, target),
            _functions.Database, _catalog.DefaultCollation, sourceType: source);
        // Unknown constants are coerced during analysis, even for an empty input.
        return unknown is not null && UnwrapCollation(unknown) is SqlLiteralExpression literal
            ? new SqlBoundConstant(SqlCastConverter.Convert(literal.Value, cast, _cancellationToken)) : cast;
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

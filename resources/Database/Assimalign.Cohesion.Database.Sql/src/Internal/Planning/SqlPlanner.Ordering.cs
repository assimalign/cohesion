using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Runtime.CompilerServices;

using Assimalign.Cohesion.Database.Sql.Language;

namespace Assimalign.Cohesion.Database.Sql.Internal;

internal sealed partial class SqlPlanner
{
    private static bool NullsFirst(SqlOrderByColumn order) => order.NullPlacement switch
    {
        SqlNullPlacement.First => true,
        SqlNullPlacement.Last => false,
        _ => !order.IsDescending,
    };

    /// <summary>
    /// Binds ORDER BY aliases and standalone numeric ordinals to completed output
    /// slots. Shared by stored, joined, virtual and grouped relations. Compound
    /// numeric expressions remain expressions: ORDER BY 1 + 1 is not ordinal 2.
    /// </summary>
    private Dictionary<SqlExpression, int> BindOrderByProjections(
        SqlSelectExpression select, IReadOnlyList<SqlProjection> projections, int wildcardColumnCount)
    {
        var aliases = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        int projectionIndex = 0;
        foreach (var column in select.Columns)
        {
            if (column.Alias is { } alias)
            {
                // An unused duplicate is harmless; referencing it is ambiguous.
                if (!aliases.TryAdd(alias, projectionIndex))
                {
                    aliases[alias] = -1;
                }
            }
            projectionIndex += column.Expression is SqlStarExpression ? wildcardColumnCount : 1;
        }

        var slots = new Dictionary<SqlExpression, int>();
        foreach (var order in select.OrderBy)
        {
            var literal = order.Expression as SqlLiteralExpression;
            bool negative = order.Expression is SqlUnaryExpression
            {
                Operator: SqlUnaryOperator.Negate, Operand: SqlLiteralExpression,
            };
            if (negative)
            {
                literal = (SqlLiteralExpression)((SqlUnaryExpression)order.Expression).Operand;
            }
            if (literal is { LiteralType: SqlLiteralType.Integer or SqlLiteralType.Float })
            {
                string text = negative ? $"-{literal.Value}" : literal.Value;
                if (literal.LiteralType != SqlLiteralType.Integer)
                {
                    throw new SqlPlanningException("CS02018", string.Format(System.Globalization.CultureInfo.CurrentCulture, ErrorMessages.BindOrderByProjectionsSqlPlanning, text), order.Expression.Location);
                }
                if (negative || literal.Value.TrimStart('0').Length == 0)
                {
                    throw new SqlPlanningException("CS02018", string.Format(System.Globalization.CultureInfo.CurrentCulture, ErrorMessages.BindOrderByProjectionsSqlPlanning2, text), order.Expression.Location);
                }
                if (!int.TryParse(literal.Value, NumberStyles.None, CultureInfo.InvariantCulture, out int ordinal)
                    || ordinal > projections.Count)
                {
                    throw new SqlPlanningException("CS02018", string.Format(System.Globalization.CultureInfo.CurrentCulture, ErrorMessages.BindOrderByProjectionsSqlPlanning3, text, projections.Count), order.Expression.Location);
                }
                slots.Add(order.Expression, ordinal - 1);
            }
            else
            {
                BindAliases(order.Expression);
            }
        }
        return slots;

        void BindAliases(SqlExpression expression)
        {
            RuntimeHelpers.EnsureSufficientExecutionStack();
            if (expression is SqlColumnReferenceExpression { TableAlias: null, SchemaName: null } reference
                && aliases.TryGetValue(reference.ColumnName, out int index))
            {
                if (index < 0)
                {
                    throw new SqlPlanningException("CS01019", string.Format(System.Globalization.CultureInfo.CurrentCulture, ErrorMessages.BindOrderByProjectionsSqlPlanning4, reference.ColumnName), reference.Location);
                }
                slots.Add(expression, index);
                return;
            }
            // Aggregate operands belong to the input scope, before projection.
            // Subquery SELECTs are also opaque to Children and bind independently.
            if (expression is SqlFunctionCallExpression call && IsAggregate(call))
            {
                return;
            }
            foreach (var child in Children(expression))
            {
                BindAliases(child);
            }
        }
    }
}

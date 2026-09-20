using System;
using System.Collections.Generic;
using System.Globalization;

using Assimalign.Cohesion.Database.Sql.Language;

namespace Assimalign.Cohesion.Database.Sql.Internal;

internal sealed partial class SqlPlanner
{
    /// <summary>
    /// Binds ORDER BY aliases and standalone numeric ordinals to completed output
    /// slots. Shared by stored, joined, virtual and grouped relations. Compound
    /// numeric expressions remain expressions: ORDER BY 1 + 1 is not ordinal 2.
    /// </summary>
    private static Dictionary<SqlExpression, int> BindOrderByProjections(
        SqlSelectExpression select, IReadOnlyList<SqlProjection> projections, int sourceColumnCount)
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
            projectionIndex += column.Expression is SqlStarExpression ? sourceColumnCount : 1;
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
                    throw new DatabaseException($"ORDER BY ordinal '{text}' must be an integer.");
                }
                if (negative || literal.Value.TrimStart('0').Length == 0)
                {
                    throw new DatabaseException($"ORDER BY ordinal '{text}' must be a positive integer.");
                }
                if (!int.TryParse(literal.Value, NumberStyles.None, CultureInfo.InvariantCulture, out int ordinal)
                    || ordinal > projections.Count)
                {
                    throw new DatabaseException($"ORDER BY ordinal '{text}' is out of range for {projections.Count} output columns.");
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
            if (expression is SqlColumnReferenceExpression { TableAlias: null, SchemaName: null } reference
                && aliases.TryGetValue(reference.ColumnName, out int index))
            {
                if (index < 0)
                {
                    throw new DatabaseException($"Ambiguous ORDER BY alias '{reference.ColumnName}'.");
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

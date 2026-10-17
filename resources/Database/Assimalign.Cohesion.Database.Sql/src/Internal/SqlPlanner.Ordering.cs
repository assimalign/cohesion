using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Runtime.CompilerServices;

using Assimalign.Cohesion.Database.Sql.Language;

namespace Assimalign.Cohesion.Database.Sql.Internal;

internal sealed partial class SqlPlanner
{
    /// <summary>The #1112 pin follows resolved key dependencies, including output aliases and ordinals.</summary>
    private void RejectDerivedOrdering(SqlSelectExpression select, IReadOnlyList<SqlProjection> projections,
        IReadOnlyDictionary<SqlExpression, int> outputSlots, SqlExpressionEvaluator scope,
        IReadOnlyList<SqlRelationBinding>? bindings)
    {
        if (InDerivedBody || bindings is null || !bindings.Any(binding => binding.Source.Derived is not null)) { return; }
        foreach (var order in select.OrderBy)
        {
            if (ReadsDerived(order.Expression, true))
            {
                throw new SqlUnsupportedQueryException(ErrorMessages.DerivedOutputOrdering, location: order.Expression.Location);
            }
        }

        bool DerivedOrdinal(int ordinal) => bindings.Any(binding => binding.Source.Derived is not null &&
            ordinal >= binding.Offset && ordinal < binding.Offset + binding.Source.Columns.Count);

        bool ReadsDerived(SqlExpression expression, bool outputs)
        {
            RuntimeHelpers.EnsureSufficientExecutionStack();
            if (outputs && outputSlots.TryGetValue(expression, out int index))
            {
                var projection = projections[index];
                return projection.ColumnOrdinal is int ordinal ? DerivedOrdinal(ordinal)
                    : projection.Expression is { } source ? ReadsDerived(source, false) : ReadsMerged(projection.Value);
            }
            if (expression is SqlColumnReferenceExpression reference) { return DerivedOrdinal(scope.ResolveColumn(reference)); }
            foreach (var child in Children(expression))
            {
                if (ReadsDerived(child, outputs)) { return true; }
            }
            return false;
        }

        bool ReadsMerged(SqlBoundExpression? value) => value switch
        {
            SqlBoundColumn column => DerivedOrdinal(column.Ordinal),
            SqlBoundCast cast => ReadsMerged(cast.Operand),
            _ => false,
        };
    }

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
                    throw new SqlPlanningException("COHSQLE018", $"ORDER BY ordinal '{text}' must be an integer.", order.Expression.Location);
                }
                if (negative || literal.Value.TrimStart('0').Length == 0)
                {
                    throw new SqlPlanningException("COHSQLE018", $"ORDER BY ordinal '{text}' must be a positive integer.", order.Expression.Location);
                }
                if (!int.TryParse(literal.Value, NumberStyles.None, CultureInfo.InvariantCulture, out int ordinal)
                    || ordinal > projections.Count)
                {
                    throw new SqlPlanningException("COHSQLE018", $"ORDER BY ordinal '{text}' is out of range for {projections.Count} output columns.", order.Expression.Location);
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
                    throw new SqlPlanningException("COHSQLE019", $"Ambiguous ORDER BY alias '{reference.ColumnName}'.", reference.Location);
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

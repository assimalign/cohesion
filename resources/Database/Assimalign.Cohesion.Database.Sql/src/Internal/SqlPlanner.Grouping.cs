using System;
using System.Collections.Generic;
using System.Linq;

using Assimalign.Cohesion.Database.Sql.Catalog;
using Assimalign.Cohesion.Database.Sql.Language;
using Assimalign.Cohesion.Database.Types;

namespace Assimalign.Cohesion.Database.Sql.Internal;

internal sealed partial class SqlPlanner
{
    /// <summary>Binds aggregation above a normal filtered relation plan.</summary>
    private SqlGroupPlan PlanGroup(SqlSelectExpression select, SqlCatalogTable? table,
        SqlSystemViewDefinition? view, IReadOnlyList<SqlCatalogColumn> columns,
        IReadOnlyList<SqlTableBinding>? bindings, SqlExpressionEvaluator evaluator)
    {
        foreach (var key in select.GroupBy)
        {
            if (ContainsAggregate(key))
            {
                throw new DatabaseException("Aggregate functions are not allowed in GROUP BY.");
            }
            ValidateExpression(key, evaluator, _subqueryTypes);
        }
        if (select.Where is not null)
        {
            ValidateExpression(select.Where, evaluator, _subqueryTypes);
        }

        var aggregates = new List<SqlFunctionCallExpression>();
        var slots = new Dictionary<SqlExpression, int>();
        var projections = new List<SqlProjection>();
        foreach (var column in select.Columns)
        {
            Bind(column.Expression);
            string name = column.Alias ?? (column.Expression switch
            {
                SqlColumnReferenceExpression reference => reference.ColumnName,
                SqlFunctionCallExpression call when IsAggregate(call) => call.FunctionName.ToLowerInvariant(),
                _ => $"column{projections.Count + 1}",
            });
            projections.Add(new SqlProjection(name, null, column.Expression, GroupExpressionType(column.Expression, columns, evaluator)));
        }
        if (select.Having is not null)
        {
            Bind(select.Having);
        }

        // ORDER BY accepts an output alias, with the same completed group values
        // as the projection. Alias slots follow the keys and all aggregates.
        var aliases = new List<(SqlExpression Expression, int Projection)>();
        foreach (var order in select.OrderBy)
        {
            if (order.Expression is SqlColumnReferenceExpression { TableAlias: null, SchemaName: null } reference)
            {
                var matches = select.Columns.Select((column, index) => (column, index))
                    .Where(pair => string.Equals(pair.column.Alias, reference.ColumnName, StringComparison.OrdinalIgnoreCase)).ToArray();
                if (matches.Length > 1)
                {
                    throw new DatabaseException($"Ambiguous ORDER BY alias '{reference.ColumnName}'.");
                }
                if (matches.Length == 1)
                {
                    aliases.Add((order.Expression, matches[0].index));
                    continue;
                }
            }
            Bind(order.Expression);
        }
        foreach (var alias in aliases)
        {
            slots[alias.Expression] = select.GroupBy.Count + aggregates.Count + alias.Projection;
        }

        var source = columns.Select((column, index) => new SqlProjection(column.Name, index, null, column.Type.Type)).ToArray();
        SqlPlan input = bindings is not null
            ? new SqlJoinPlan(bindings, columns, select.Joins[0].Condition!, source, select.Where,
                [], null, null, false, SelectJoinAccessPath(bindings, select.Joins[0].Condition!, evaluator))
            : view is not null
                ? new SqlSystemViewPlan(view, source, select.Where, [], null, null, false)
                : new SqlSelectPlan(table!, source, select.Where, [], null, null, false, SelectAccessPath(table!, select.Where));

        return new SqlGroupPlan(input, columns, bindings, select.GroupBy, aggregates, slots, projections,
            select.Having, select.OrderBy, EvaluateCount(select.Limit, "LIMIT"), EvaluateCount(select.Offset, "OFFSET"), select.IsDistinct);

        void Bind(SqlExpression expression)
        {
            if (expression is SqlFunctionCallExpression call && IsAggregate(call))
            {
                if (call.Arguments.Count != 1 || call.Arguments[0] is SqlStarExpression
                    && !call.FunctionName.Equals("COUNT", StringComparison.OrdinalIgnoreCase))
                {
                    throw new DatabaseException($"{call.FunctionName} requires exactly one expression; only COUNT accepts '*'.");
                }
                var argument = call.Arguments[0];
                if (ContainsAggregate(argument))
                {
                    throw new DatabaseException("Nested aggregate functions are not allowed.");
                }
                if (argument is not SqlStarExpression)
                {
                    ValidateExpression(argument, evaluator, _subqueryTypes);
                }
                if (call.FunctionName.ToUpperInvariant() is "SUM" or "AVG")
                {
                    var type = GroupExpressionType(argument, columns, evaluator);
                    if (type is not (DatabaseType.Null or DatabaseType.Int8 or DatabaseType.Int16 or DatabaseType.Int32
                        or DatabaseType.Int64 or DatabaseType.Float32 or DatabaseType.Float64 or DatabaseType.Decimal))
                    {
                        throw new DatabaseException($"{call.FunctionName} requires a numeric argument.");
                    }
                }
                int index = aggregates.FindIndex(candidate => SameGroupExpression(candidate, call, evaluator));
                if (index < 0)
                {
                    index = aggregates.Count;
                    aggregates.Add(call);
                }
                slots[expression] = select.GroupBy.Count + index;
                return;
            }
            for (int i = 0; i < select.GroupBy.Count; i++)
            {
                if (SameGroupExpression(expression, select.GroupBy[i], evaluator))
                {
                    slots[expression] = i;
                    return;
                }
            }
            if (expression is SqlColumnReferenceExpression reference)
            {
                evaluator.ResolveColumn(reference);
                throw new DatabaseException($"Column '{reference.ColumnName}' must appear in GROUP BY or be used in an aggregate function.");
            }
            if (expression is SqlStarExpression)
            {
                throw new DatabaseException("SELECT * is not allowed in a grouped query; project GROUP BY expressions or aggregate functions.");
            }
            ValidateExpression(expression, evaluator, _subqueryTypes);
            foreach (var child in Children(expression))
            {
                Bind(child);
            }
        }
    }

    /// <summary>Recognizes the closed set of executable aggregate functions.</summary>
    private static bool IsAggregate(SqlFunctionCallExpression call)
        => call.FunctionName.ToUpperInvariant() is "COUNT" or "SUM" or "AVG" or "MIN" or "MAX";

    /// <summary>
    /// Compares expression structure after column binding. Qualified and bare
    /// references to the same column match; different operators never do.
    /// </summary>
    private bool SameGroupExpression(SqlExpression left, SqlExpression right, SqlExpressionEvaluator evaluator)
    {
        bool same = (left, right) switch
        {
            // Two subqueries are the same group key only when they are the same node;
            // structurally identical children do not make separate queries one key.
            (SqlSubqueryExpression a, SqlSubqueryExpression b) => ReferenceEquals(a, b),
            (SqlExistsExpression a, SqlExistsExpression b) => ReferenceEquals(a, b),
            (SqlColumnReferenceExpression a, SqlColumnReferenceExpression b) => evaluator.ResolveColumn(a) == evaluator.ResolveColumn(b),
            (SqlLiteralExpression a, SqlLiteralExpression b) => a.LiteralType == b.LiteralType && a.Value == b.Value,
            (SqlParameterExpression a, SqlParameterExpression b) => a.ParameterName == b.ParameterName,
            (SqlStarExpression, SqlStarExpression) => true,
            (SqlBinaryExpression a, SqlBinaryExpression b) => a.Operator == b.Operator,
            (SqlUnaryExpression a, SqlUnaryExpression b) => a.Operator == b.Operator,
            (SqlFunctionCallExpression a, SqlFunctionCallExpression b) => a.FunctionName.Equals(b.FunctionName, StringComparison.OrdinalIgnoreCase),
            (SqlCollateExpression a, SqlCollateExpression b) => Collation.FromName(a.CollationName) == Collation.FromName(b.CollationName),
            (SqlCastExpression a, SqlCastExpression b) => a.TargetTypeInfo?.Type == b.TargetTypeInfo?.Type
                && a.TargetTypeInfo?.MaxLength == b.TargetTypeInfo?.MaxLength
                && a.TargetTypeInfo?.Precision == b.TargetTypeInfo?.Precision && a.TargetTypeInfo?.Scale == b.TargetTypeInfo?.Scale,
            (SqlIsNullExpression a, SqlIsNullExpression b) => a.IsNegated == b.IsNegated,
            (SqlBetweenExpression a, SqlBetweenExpression b) => a.IsNegated == b.IsNegated,
            (SqlInExpression a, SqlInExpression b) => a.IsNegated == b.IsNegated,
            (SqlLikeExpression a, SqlLikeExpression b) => a.IsNegated == b.IsNegated,
            (SqlCaseExpression a, SqlCaseExpression b) => (a.Input is null) == (b.Input is null)
                && (a.ElseResult is null) == (b.ElseResult is null) && a.WhenClauses.Count == b.WhenClauses.Count,
            _ => false,
        };
        return same && Children(left).SequenceEqual(Children(right), new GroupExpressionComparer(this, evaluator));
    }

    /// <summary>Structural equality is used for binding only, without expression hashing.</summary>
    private sealed class GroupExpressionComparer(SqlPlanner planner, SqlExpressionEvaluator evaluator) : IEqualityComparer<SqlExpression>
    {
        public bool Equals(SqlExpression? left, SqlExpression? right)
            => left is not null && right is not null && planner.SameGroupExpression(left, right, evaluator);
        public int GetHashCode(SqlExpression expression) => 0;
    }

    /// <summary>Declares aggregate result types even when no source rows exist.</summary>
    private DatabaseType GroupExpressionType(SqlExpression expression, IReadOnlyList<SqlCatalogColumn> columns,
        SqlExpressionEvaluator evaluator) => expression switch
    {
        SqlColumnReferenceExpression column => columns[evaluator.ResolveColumn(column)].Type.Type,
        SqlSubqueryExpression or SqlExistsExpression or SqlInExpression { Subquery: not null }
            when _subqueryTypes.TryGetValue(expression, out var subqueryType) => subqueryType,
        SqlConstantExpression constant => constant.Type,
        SqlCollateExpression collate => GroupExpressionType(collate.Operand, columns, evaluator),
        SqlCastExpression cast => cast.TargetTypeInfo!.Type,
        SqlParameterExpression parameter => GroupValueType(evaluator.Evaluate(parameter, [])),
        SqlLiteralExpression literal => literal.LiteralType switch
        {
            SqlLiteralType.Integer => DatabaseType.Int64,
            SqlLiteralType.Float => DatabaseType.Decimal,
            SqlLiteralType.String => DatabaseType.String,
            SqlLiteralType.Boolean => DatabaseType.Boolean,
            _ => DatabaseType.Null,
        },
        SqlFunctionCallExpression call => call.FunctionName.ToUpperInvariant() switch
        {
            "COUNT" or "LENGTH" => DatabaseType.Int64,
            "SUM" or "AVG" => DatabaseType.Decimal,
            "UPPER" or "LOWER" => call.Arguments.Count == 1
                ? GroupExpressionType(call.Arguments[0], columns, evaluator) : DatabaseType.Null,
            "ABS" when call.Arguments.Count == 1 => GroupExpressionType(call.Arguments[0], columns, evaluator) switch
            {
                DatabaseType.Int8 or DatabaseType.Int16 or DatabaseType.Int32 or DatabaseType.Int64 => DatabaseType.Int64,
                var type => type,
            },
            "COALESCE" => call.Arguments.Select(argument => GroupExpressionType(argument, columns, evaluator))
                .Aggregate(DatabaseType.Null, CommonGroupType),
            _ => call.Arguments.Count > 0 ? GroupExpressionType(call.Arguments[0], columns, evaluator) : DatabaseType.Null,
        },
        SqlUnaryExpression { Operator: SqlUnaryOperator.Not } => DatabaseType.Boolean,
        SqlUnaryExpression unary => GroupExpressionType(unary.Operand, columns, evaluator) switch
        {
            DatabaseType.Int8 or DatabaseType.Int16 or DatabaseType.Int32 or DatabaseType.Int64 => DatabaseType.Int64,
            DatabaseType.Float32 => DatabaseType.Float64,
            var type => type,
        },
        SqlBinaryExpression binary when binary.Operator == SqlBinaryOperator.Concat => DatabaseType.String,
        SqlBinaryExpression binary when binary.Operator is SqlBinaryOperator.Add or SqlBinaryOperator.Subtract
            or SqlBinaryOperator.Multiply or SqlBinaryOperator.Divide or SqlBinaryOperator.Modulo
            => GroupExpressionType(binary.Left, columns, evaluator) is DatabaseType.Decimal or DatabaseType.Float32 or DatabaseType.Float64
                || GroupExpressionType(binary.Right, columns, evaluator) is DatabaseType.Decimal or DatabaseType.Float32 or DatabaseType.Float64
                    ? DatabaseType.Decimal : DatabaseType.Int64,
        SqlBinaryExpression or SqlIsNullExpression or SqlBetweenExpression or SqlInExpression or SqlLikeExpression => DatabaseType.Boolean,
        SqlCaseExpression @case => @case.WhenClauses.Select(clause => GroupExpressionType(clause.Result, columns, evaluator))
            .Append(@case.ElseResult is null ? DatabaseType.Null : GroupExpressionType(@case.ElseResult, columns, evaluator))
            .Aggregate(DatabaseType.Null, CommonGroupType),
        _ => DatabaseType.Null,
    };

    /// <summary>Finds the numeric common type of alternative scalar results.</summary>
    private static DatabaseType CommonGroupType(DatabaseType left, DatabaseType right)
    {
        if (left == DatabaseType.Null || left == right) { return right; }
        if (right == DatabaseType.Null) { return left; }
        static int Rank(DatabaseType type) => type switch
        {
            DatabaseType.Int8 => 1, DatabaseType.Int16 => 2, DatabaseType.Int32 => 3, DatabaseType.Int64 => 4,
            DatabaseType.Float32 => 5, DatabaseType.Float64 => 6, DatabaseType.Decimal => 7, _ => 0,
        };
        int a = Rank(left), b = Rank(right);
        if (a > 0 && b > 0)
        {
            return a >= b ? left : right;
        }
        throw new DatabaseException($"Grouped CASE/COALESCE results require compatible types; found {left} and {right}.");
    }

    /// <summary>Preserves parameter type metadata without reflection.</summary>
    private static DatabaseType GroupValueType(object? value) => value switch
    {
        sbyte => DatabaseType.Int8, short => DatabaseType.Int16, int => DatabaseType.Int32, long => DatabaseType.Int64,
        float => DatabaseType.Float32, double => DatabaseType.Float64, decimal => DatabaseType.Decimal,
        string => DatabaseType.String, bool => DatabaseType.Boolean, byte[] => DatabaseType.Binary,
        DateOnly => DatabaseType.Date, TimeOnly => DatabaseType.Time, DateTime => DatabaseType.DateTime,
        DateTimeOffset => DatabaseType.DateTimeOffset, TimeSpan => DatabaseType.TimeSpan, Guid => DatabaseType.Guid,
        _ => DatabaseType.Null,
    };
}

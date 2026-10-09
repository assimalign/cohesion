using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.CompilerServices;

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

        var aggregates = new List<(SqlFunctionCallExpression Call, SqlAggregateFunction Function)>();
        var slots = new Dictionary<SqlExpression, int>();
        var projections = new List<SqlProjection>();
        foreach (var column in select.Columns)
        {
            BindSlots(column.Expression);
            string name = column.Alias ?? (column.Expression switch
            {
                SqlColumnReferenceExpression reference => reference.ColumnName,
                SqlFunctionCallExpression call when IsAggregate(call) => call.FunctionName.ToLowerInvariant(),
                _ => $"column{projections.Count + 1}",
            });
            projections.Add(new SqlProjection(name, null, column.Expression, GroupExpressionType(column.Expression, columns, evaluator))
            {
                // A bare COUNT never returns NULL, even over no rows; every other projection may.
                IsNullable = !(column.Expression is SqlFunctionCallExpression projected && IsAggregate(projected)
                    && aggregates[slots[projected] - select.GroupBy.Count].Function.IsNeverNull),
            });
        }
        if (select.Having is not null)
        {
            BindSlots(select.Having);
        }

        var orderByProjections = BindOrderByProjections(select, projections, columns.Count);
        foreach (var order in select.OrderBy)
        {
            BindSlots(order.Expression, orderByProjections);
        }

        // Every slot is known now. The input relation, the keys and the aggregates' operands bind
        // over the source row; HAVING, the projections and ORDER BY over the grouped row (keys,
        // then aggregate results, then the projected outputs that ordering may name). Both scopes
        // resolve columns through the join bindings alone, as grouped execution always has.
        var sourceScope = new SqlExpressionEvaluator(columns, _parameters, bindings, defaultCollation: _catalog.DefaultCollation,
            subquerySlots: _subquerySlots, functions: _functions);
        var groupScope = new SqlExpressionEvaluator(columns, _parameters, bindings, slots, _catalog.DefaultCollation,
            subquerySlots: _subquerySlots, functions: _functions);

        var source = columns.Select((column, index) => PassThrough(column.Name, index, columns, sourceScope)).ToArray();
        var where = select.Where is null ? null : evaluator.Bind(select.Where);
        SqlPlan input = bindings is not null
            ? new SqlJoinPlan(bindings, columns, evaluator.Bind(select.Joins[0].Condition!), source, where,
                [], null, null, false, SelectJoinAccessPath(bindings, select.Joins[0].Condition!, evaluator))
            : view is not null
                ? new SqlSystemViewPlan(view, source, where, [], null, null, false)
                : new SqlSelectPlan(table!, source, where, [], null, null, false, SelectAccessPath(table!, select.Where));

        var keys = new SqlBoundKey[select.GroupBy.Count];
        for (int i = 0; i < keys.Length; i++)
        {
            var value = sourceScope.Bind(select.GroupBy[i], out SqlBoundCollation collation);
            keys[i] = new SqlBoundKey(value, collation);
        }

        var boundAggregates = new SqlGroupAggregate[aggregates.Count];
        for (int i = 0; i < boundAggregates.Length; i++)
        {
            var (call, function) = aggregates[i];
            var arguments = sourceScope.BindArguments(call.Arguments, out SqlBoundCollation collation);
            boundAggregates[i] = new SqlGroupAggregate(call, function, arguments,
                SqlFunctionResolver.CoercionTargets(function, arguments.Length), collation, _functions.Database);
        }

        for (int i = 0; i < projections.Count; i++)
        {
            var value = groupScope.Bind(projections[i].Expression!, out SqlBoundCollation collation);
            projections[i] = projections[i] with { Value = value, Collation = collation };
        }

        var having = select.Having is null ? null : groupScope.Bind(select.Having);
        var ordering = BindOrdering(select.OrderBy,
            groupScope.ForOrdering(projections, orderByProjections, keys.Length + boundAggregates.Length));

        return new SqlGroupPlan(input, columns, bindings, keys, boundAggregates, projections,
            having, ordering, EvaluateCount(select.Limit, "LIMIT"), EvaluateCount(select.Offset, "OFFSET"),
            select.IsDistinct);

        void BindSlots(SqlExpression expression, IReadOnlyDictionary<SqlExpression, int>? outputSlots = null)
        {
            RuntimeHelpers.EnsureSufficientExecutionStack();
            if (outputSlots is not null && outputSlots.ContainsKey(expression))
            {
                return;
            }
            if (expression is SqlFunctionCallExpression call && IsAggregate(call))
            {
                // The overload is chosen by its arguments' types over the input row: SUM and AVG
                // take a numeric argument, so SUM over text fails here with COHSQLE006, whatever
                // the rows (#1189). The grouping executor creates the chosen function's accumulators.
                var function = (SqlAggregateFunction)evaluator.ResolveFunction(call)!;
                foreach (var argument in call.Arguments)
                {
                    if (ContainsAggregate(argument))
                    {
                        throw new DatabaseException("Nested aggregate functions are not allowed.");
                    }
                    if (argument is not SqlStarExpression)
                    {
                        ValidateExpression(argument, evaluator, _subqueryTypes);
                    }
                }
                int index = aggregates.FindIndex(candidate => SameGroupExpression(candidate.Call, call, evaluator));
                if (index < 0)
                {
                    index = aggregates.Count;
                    aggregates.Add((call, function));
                }
                slots[expression] = select.GroupBy.Count + index;
                return;
            }
            for (int i = 0; i < select.GroupBy.Count; i++)
            {
                // An alias-bearing expression is evaluated over completed outputs;
                // comparing it against source keys would rebind the alias as a column.
                if (!ContainsOutput(expression) && SameGroupExpression(expression, select.GroupBy[i], evaluator))
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
            ValidateExpression(expression, evaluator, _subqueryTypes, outputSlots);
            foreach (var child in Children(expression))
            {
                BindSlots(child, outputSlots);
            }

            bool ContainsOutput(SqlExpression candidate)
            {
                RuntimeHelpers.EnsureSufficientExecutionStack();
                return outputSlots is not null && (outputSlots.ContainsKey(candidate) || Children(candidate).Any(ContainsOutput));
            }
        }
    }

    /// <summary>Recognizes a call to an aggregate of the engine's function catalog, by its name.</summary>
    private bool IsAggregate(SqlFunctionCallExpression call) => _functions.Catalog.IsAggregate(call.FunctionName);

    /// <summary>
    /// Compares expression structure after column binding. Qualified and bare
    /// references to the same column match; different operators never do.
    /// </summary>
    private bool SameGroupExpression(SqlExpression left, SqlExpression right, SqlExpressionEvaluator evaluator)
    {
        RuntimeHelpers.EnsureSufficientExecutionStack();
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
            // The child comparison below also requires the same number of terms.
            (SqlLogicalExpression a, SqlLogicalExpression b) => a.Operator == b.Operator,
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
    private sealed class GroupExpressionComparer : IEqualityComparer<SqlExpression>
    {
        private readonly SqlPlanner _planner;
        private readonly SqlExpressionEvaluator _evaluator;

        /// <summary>Initializes a new instance of the <see cref="GroupExpressionComparer"/> class.</summary>
        /// <param name="planner">The planner whose structural comparison decides equality.</param>
        /// <param name="evaluator">The evaluator that binds column references during comparison.</param>
        public GroupExpressionComparer(SqlPlanner planner, SqlExpressionEvaluator evaluator)
        {
            _planner = planner;
            _evaluator = evaluator;
        }

        public bool Equals(SqlExpression? left, SqlExpression? right)
            => left is not null && right is not null && _planner.SameGroupExpression(left, right, _evaluator);
        public int GetHashCode(SqlExpression expression) => 0;
    }

    /// <summary>Declares aggregate result types even when no source rows exist.</summary>
    private DatabaseType GroupExpressionType(SqlExpression expression, IReadOnlyList<SqlCatalogColumn> columns,
        SqlExpressionEvaluator evaluator)
    {
        RuntimeHelpers.EnsureSufficientExecutionStack();
        return GroupExpressionTypeCore(expression, columns, evaluator);
    }

    private DatabaseType GroupExpressionTypeCore(SqlExpression expression, IReadOnlyList<SqlCatalogColumn> columns,
        SqlExpressionEvaluator evaluator) => expression switch
    {
        SqlColumnReferenceExpression column => columns[evaluator.ResolveColumn(column)].Type.Type,
        SqlSubqueryExpression or SqlExistsExpression or SqlInExpression { Subquery: not null }
            when _subqueryTypes.TryGetValue(expression, out var subqueryType) => subqueryType,
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
        SqlFunctionCallExpression call when SqlStandardLibrary.IsCoalesce(call.FunctionName)
            => CoalesceGroupType(call.Arguments, columns, evaluator),
        SqlFunctionCallExpression call => CallResultType(call, columns, evaluator),
        SqlUnaryExpression { Operator: SqlUnaryOperator.Not } => DatabaseType.Boolean,
        // Unary plus returns its operand unchanged; only negation widens exact integers.
        SqlUnaryExpression { Operator: SqlUnaryOperator.Plus } plus => GroupExpressionType(plus.Operand, columns, evaluator),
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
        SqlLogicalExpression or SqlBinaryExpression or SqlIsNullExpression or SqlBetweenExpression or SqlInExpression
            or SqlLikeExpression => DatabaseType.Boolean,
        SqlCaseExpression @case => CaseGroupType(@case, columns, evaluator),
        _ => DatabaseType.Null,
    };

    /// <summary>
    /// A call's declared output type: the result type of the overload it resolves to, a polymorphic
    /// result typed from the arguments' output types (<c>UPPER(name)</c> is text, <c>MAX(amount)</c>
    /// has the amount's type, <c>ABS</c> of an integer is BIGINT). A call outside the catalog keeps
    /// the type of its first argument, as before.
    /// </summary>
    private DatabaseType CallResultType(SqlFunctionCallExpression call, IReadOnlyList<SqlCatalogColumn> columns,
        SqlExpressionEvaluator evaluator)
    {
        var types = new DatabaseType[call.Arguments.Count];
        for (int index = 0; index < types.Length; index++)
        {
            types[index] = GroupExpressionType(call.Arguments[index], columns, evaluator);
        }

        SqlFunction? function;
        try
        {
            function = evaluator.ResolveFunction(call);
        }
        catch (SqlEvaluationException)
        {
            function = null;
        }

        return function is not null ? SqlFunctionResolver.ResultType(function, types)
            : types.Length > 0 ? types[0] : DatabaseType.Null;
    }

    // CASE and COALESCE fold their alternatives in loops, not LINQ chains. A chain adds an
    // iterator, an aggregate and a lambda frame per nesting level, and a closure on every call,
    // so this walk ran out of stack before the parser on a statement the parser had read
    // (SqlExpressionDepthExecutionTests: the parser limits how deeply a statement nests).

    /// <summary>Finds the common type of a CASE's results: its clauses in order, then its ELSE.</summary>
    private DatabaseType CaseGroupType(SqlCaseExpression @case, IReadOnlyList<SqlCatalogColumn> columns,
        SqlExpressionEvaluator evaluator)
    {
        // The ELSE is typed before the clauses, as the chain this replaces did, so a failure in
        // either surfaces in the same order.
        DatabaseType elseType = @case.ElseResult is null ? DatabaseType.Null : GroupExpressionType(@case.ElseResult, columns, evaluator);
        DatabaseType type = DatabaseType.Null;
        IReadOnlyList<SqlWhenClause> clauses = @case.WhenClauses;
        for (int index = 0; index < clauses.Count; index++)
        {
            type = CommonGroupType(type, GroupExpressionType(clauses[index].Result, columns, evaluator));
        }

        return CommonGroupType(type, elseType);
    }

    /// <summary>Finds the common type of COALESCE's arguments, in order.</summary>
    private DatabaseType CoalesceGroupType(IReadOnlyList<SqlExpression> arguments, IReadOnlyList<SqlCatalogColumn> columns,
        SqlExpressionEvaluator evaluator)
    {
        DatabaseType type = DatabaseType.Null;
        for (int index = 0; index < arguments.Count; index++)
        {
            type = CommonGroupType(type, GroupExpressionType(arguments[index], columns, evaluator));
        }

        return type;
    }

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

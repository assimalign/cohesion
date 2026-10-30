using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.CompilerServices;

using Assimalign.Cohesion.Database.Sql.Catalog;
using Assimalign.Cohesion.Database.Sql.Language;
using Assimalign.Cohesion.Database.Types;

using Assimalign.Cohesion.Database.Execution.Internal;

namespace Assimalign.Cohesion.Database.Sql.Internal;

internal sealed partial class SqlPlanner
{
    private SqlPlan PlanGroup(SqlSelectExpression select, SqlCatalogTable? table,
        SqlSystemViewDefinition? view, IReadOnlyList<SqlCatalogColumn> columns,
        IReadOnlyList<SqlRelationBinding>? bindings, SqlExpressionEvaluator evaluator, IReadOnlyList<SqlJoinStep> steps, SqlJoinTree? tree = null)
    {
        bool extended = false;
        extended |= _queryScopes?.GroupingCalls?.TryGetValue(select, out var outerCalls) == true && outerCalls.Count > 0;
        for (int i = 0; i < select.GroupBy.Count; i++) { extended |= select.GroupBy[i] is SqlGroupingSetExpression; }
        for (int i = 0; i < select.Columns.Count; i++) { extended |= ContainsGrouping(select.Columns[i].Expression); }
        if (select.Having is not null) { extended |= ContainsGrouping(select.Having); }
        for (int i = 0; i < select.OrderBy.Count; i++) { extended |= ContainsGrouping(select.OrderBy[i].Expression); }
        for (int i = 0; i < select.Windows.Count; i++)
        {
            var definition = select.Windows[i].Specification;
            for (int k = 0; k < definition.PartitionBy.Count; k++) { extended |= ContainsGrouping(definition.PartitionBy[k]); }
            for (int k = 0; k < definition.OrderBy.Count; k++) { extended |= ContainsGrouping(definition.OrderBy[k].Expression); }
        }
        return extended ? PlanGroupingSets(select, table, view, columns, bindings, evaluator, steps, tree)
            : PlanPlainGroup(select, table, view, columns, bindings, evaluator, steps, tree);
    }

    /// <summary>Binds aggregation above a normal filtered relation plan.</summary>
    private SqlPlan PlanPlainGroup(SqlSelectExpression select, SqlCatalogTable? table,
        SqlSystemViewDefinition? view, IReadOnlyList<SqlCatalogColumn> columns,
        IReadOnlyList<SqlRelationBinding>? bindings, SqlExpressionEvaluator evaluator, IReadOnlyList<SqlJoinStep> steps, SqlJoinTree? tree = null)
    {
        foreach (var key in select.GroupBy)
        {
            if (ContainsAggregate(key))
            {
                throw new SqlPlanningException("CS05017", ErrorMessages.SqlPlannerPlanGroupSqlPlanning, key.Location);
            }
            ValidateExpression(key, evaluator, _subqueryTypes);
        }
        if (select.Where is not null)
        {
            ValidatePredicate(select.Where, evaluator, "WHERE", _subqueryTypes);
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
                HasOutputName = column.Alias is not null || column.Expression is SqlColumnReferenceExpression ||
                    column.Expression is SqlFunctionCallExpression namedCall && IsAggregate(namedCall),
                MaxLength = evaluator.MaximumLengthOf(column.Expression),
                // A bare COUNT never returns NULL, even over no rows; every other projection may.
                IsNullable = !(column.Expression is SqlFunctionCallExpression projected && IsAggregate(projected)
                    && aggregates[slots[projected] - select.GroupBy.Count].Function.IsNeverNull),
            });
        }
        foreach (var definition in select.Windows)
        {
            foreach (var key in definition.Specification.PartitionBy) { BindSlots(key); }
            foreach (var key in definition.Specification.OrderBy) { BindSlots(key.Expression); }
        }
        if (select.Having is not null)
        {
            BindSlots(select.Having);
        }

        var orderByProjections = BindOrderByProjections(select, projections, columns.Count);
        CaptureViewOrdering(select, columns, bindings, evaluator, orderByProjections);
        foreach (var order in select.OrderBy)
        {
            BindSlots(order.Expression, orderByProjections);
        }

        // Every slot is known now. The input relation, the keys and the aggregates' operands bind
        // over the source row; HAVING, the projections and ORDER BY over the grouped row (keys,
        // then aggregate results, then the projected outputs that ordering may name). Both scopes
        // resolve columns through the same relation references as the input scope.
        var sourceScope = new SqlExpressionEvaluator(columns, _parameters, bindings, defaultCollation: _catalog.DefaultCollation,
            subquerySlots: _subquerySlots, functions: _functions, cancellationToken: _cancellationToken, foldConstants: FoldQueryConstants,
            outerScope: OuterScope, outerDependencies: OuterDependencies, relation: select.From.Count == 0 ? null : select.From[0].Table);
        // A call over a key or an aggregate result resolves by the slot's type over the input row,
        // as the same call does outside a grouping: describe(COUNT(*)) is describe(BIGINT).
        var groupScope = new SqlExpressionEvaluator(columns, _parameters, bindings, slots, _catalog.DefaultCollation,
            subquerySlots: _subquerySlots, functions: _functions, cancellationToken: _cancellationToken, foldConstants: FoldQueryConstants,
            outerScope: OuterScope, outerDependencies: OuterDependencies, slotScope: sourceScope, relation: select.From.Count == 0 ? null : select.From[0].Table);

        var source = columns.Select((column, index) => PassThrough(column.Name, index, columns, sourceScope)).ToArray();
        var where = select.Where is null ? null : evaluator.BindPredicate(select.Where);
        SqlPlan input = bindings is not null
            ? PushJoinPredicates(new SqlJoinPlan(bindings, columns, steps, source, where, [], null, null, false) { Tree = tree })
            : view is not null
                ? new SqlSystemViewPlan(view, source, where, [], null, null, false)
                : table is null
                    ? new SqlConstantPlan(source, where, [], null, null, false, null)
                    : new SqlSelectPlan(table, source, where, [], null, null, false, SelectAccessPath(table, select.Where, OuterScope is null ? null : evaluator));

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
            SqlBoundExpression[] arguments;
            SqlBoundCollation collation;
            if (function is SqlStringAggregateFunction)
            {
                arguments = new SqlBoundExpression[call.Arguments.Count];
                arguments[0] = sourceScope.Bind(call.Arguments[0], out collation);
                for (int j = 1; j < arguments.Length; j++) { arguments[j] = sourceScope.Bind(call.Arguments[j]); }
            }
            else
            {
                arguments = sourceScope.BindArguments(call.Arguments, out collation,
                    statisticsInput: function is SqlStatisticsFunction);
            }
            sourceScope.BindParameterCoercions(function, arguments, collation);
            boundAggregates[i] = new SqlGroupAggregate(call, function, arguments,
                SqlFunctionResolver.CoercionTargets(function, arguments.Length), collation, _functions.Database, BindOrdering(call.OrderBy, sourceScope),
                call.Quantifier == SqlSetQuantifier.Distinct ? call.Arguments.Select(sourceScope.BindCollation).ToArray() : [],
                call.Filter is null ? null : sourceScope.BindPredicate(call.Filter));
        }

        var windows = HasWindows(select) ? BindWindows(select, groupScope, slots, keys.Length + boundAggregates.Length) : null;
        for (int i = 0; i < projections.Count; i++)
        {
            var value = groupScope.Bind(projections[i].Expression!, out SqlBoundCollation collation, out var candidate);
            projections[i] = projections[i] with { Value = value, Collation = collation,
                Metadata = CaptureProjectionMetadata(candidate,
                    UnwrapCollation(projections[i].Expression!) is SqlColumnReferenceExpression column ? evaluator.ColumnMetadata(column) : null,
                    projections[i].Expression, evaluator) };
        }

        if (select.Having is not null) { ValidatePredicate(select.Having, groupScope, "HAVING", _subqueryTypes); }
        var having = select.Having is null ? null : groupScope.BindPredicate(select.Having);
        var ordering = BindOrdering(select.OrderBy,
            groupScope.ForOrdering(projections, orderByProjections, keys.Length + boundAggregates.Length));

        var result = new SqlGroupPlan(input, columns, bindings, keys, boundAggregates, projections,
            having, ordering, BindCount(select.Limit, LimitClause(select)), BindCount(select.Offset, "OFFSET"),
            select.IsDistinct);
        return windows is null ? result : FinishWindowGroup(select, result, groupScope, windows, keys.Length + boundAggregates.Length, orderByProjections);

        void BindSlots(SqlExpression expression, IReadOnlyDictionary<SqlExpression, int>? outputSlots = null)
        {
            RuntimeHelpers.EnsureSufficientExecutionStack();
            if (outputSlots is not null && outputSlots.ContainsKey(expression))
            {
                return;
            }
            if (expression is SqlWindowExpression window)
            {
                foreach (var argument in Children(window.Function)) { if (argument is not SqlStarExpression) { BindSlots(argument); } }
                var windowKeys = WindowKeysOf(window, NamedWindows(select));
                foreach (var key in windowKeys.Partition) { BindSlots(key); }
                foreach (var key in windowKeys.Order) { BindSlots(key.Expression); }
                return;
            }
            if (expression is SqlFunctionCallExpression call && IsAggregate(call))
            {
                if (call.Filter is not null)
                {
                    if (ContainsAggregate(call.Filter))
                    {
                        throw new SqlPlanningException("CS05073", ErrorMessages.FilterContainsAggregate, call.Filter.Location);
                    }
                    ValidatePredicate(call.Filter, evaluator, "FILTER", _subqueryTypes);
                }
                // The overload is chosen by its arguments' types over the input row: SUM and AVG
                // take a numeric argument, so SUM over text fails here with CS01006, whatever
                // the rows (#1189). The grouping executor creates the chosen function's accumulators.
                if (call.FunctionName.Equals("LISTAGG", StringComparison.OrdinalIgnoreCase) &&
                    (!call.HasWithinGroupOrdering || call.OrderBy.Count == 0))
                {
                    throw new SqlPlanningException("CS00003", ErrorMessages.ListaggRequiresWithinGroup, call.Location);
                }
                var function = (SqlAggregateFunction)evaluator.ResolveFunction(call)!;
                foreach (var argument in Children(call))
                {
                    if (ContainsAggregate(argument))
                    {
                        throw new SqlPlanningException("CS05017", ErrorMessages.SqlPlannerPlanGroupSqlPlanning2, call.Location);
                    }
                    if (argument is not SqlStarExpression)
                    {
                        ValidateExpression(argument, evaluator, _subqueryTypes);
                    }
                }
                if (call.Quantifier == SqlSetQuantifier.Distinct && call.OrderBy.Any(key =>
                    !call.Arguments.Any(argument => SameGroupExpression(argument, key.Expression, evaluator))))
                {
                    throw new SqlPlanningException("CS05032", ErrorMessages.DistinctOrderingArgument, call.Location);
                }
                if (function is SqlStringAggregateFunction && call.Arguments.Count > 1 &&
                    call.Arguments[1] is not (SqlLiteralExpression { LiteralType: SqlLiteralType.String or SqlLiteralType.Null } or SqlParameterExpression))
                {
                    throw new SqlUnsupportedQueryException(ErrorMessages.StringAggregateDelimiter, location: call.Arguments[1].Location);
                }
                bool volatileInputs = call.Filter is not null && Children(call).Any(HasVolatileInput);
                int index = volatileInputs ? -1 : aggregates.FindIndex(candidate => SameGroupExpression(candidate.Call, call, evaluator));
                if (index < 0)
                {
                    index = aggregates.Count;
                    aggregates.Add((call, function));
                }
                slots[expression] = select.GroupBy.Count + index;
                return;

                bool HasVolatileInput(SqlExpression input)
                {
                    RuntimeHelpers.EnsureSufficientExecutionStack();
                    // Child query bodies have their own binding; conservatively retain their
                    // separate invocation rather than sharing a possibly volatile result.
                    if (input is SqlSubqueryExpression or SqlExistsExpression or SqlQuantifiedComparisonExpression or SqlInExpression { Subquery: not null }) { return true; }
                    if (input is SqlFunctionCallExpression functionCall &&
                        evaluator.ResolveFunction(functionCall)?.Volatility == SqlFunctionVolatility.Volatile) { return true; }
                    return Children(input).Any(HasVolatileInput);
                }
            }
            for (int i = 0; i < select.GroupBy.Count; i++)
            {
                // An alias-bearing expression is evaluated over completed outputs;
                // comparing it against source keys would rebind the alias as a column.
                if (!ContainsOutput(expression) && SameGroupExpression(expression, select.GroupBy[i], evaluator))
                {
                    ValidateExpression(expression, evaluator, _subqueryTypes);
                    slots[expression] = i;
                    return;
                }
            }
            if (expression is SqlColumnReferenceExpression reference)
            {
                if (evaluator.IsOuterColumn(reference)) { return; }
                throw new SqlPlanningException("CS05017", string.Format(System.Globalization.CultureInfo.CurrentCulture, ErrorMessages.SqlPlannerPlanGroupSqlPlanning3, reference.ColumnName), reference.Location);
            }
            if (expression is SqlStarExpression)
            {
                throw new SqlPlanningException("CS05017", ErrorMessages.SqlPlannerPlanGroupSqlPlanning4, expression.Location);
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
            // Repeated subqueries in one scope share a group key when their complete bodies
            // match structurally. The ordinary child comparison still binds scalar operands.
            (SqlSubqueryExpression a, SqlSubqueryExpression b) => SqlPersistedExpression.SameQuery(a.Select, b.Select),
            (SqlExistsExpression a, SqlExistsExpression b) => a.IsNegated == b.IsNegated && SqlPersistedExpression.SameQuery(a.Subquery, b.Subquery),
            (SqlQuantifiedComparisonExpression a, SqlQuantifiedComparisonExpression b) => a.Operator == b.Operator &&
                a.Quantifier == b.Quantifier && SqlPersistedExpression.SameQuery(a.Subquery, b.Subquery),
            (SqlColumnReferenceExpression a, SqlColumnReferenceExpression b) => SameColumn(a, b),
            (SqlLiteralExpression a, SqlLiteralExpression b) => a.LiteralType == b.LiteralType && a.Value == b.Value,
            (SqlParameterExpression a, SqlParameterExpression b) => ParameterBindings.BareName(a.ParameterName) == ParameterBindings.BareName(b.ParameterName),
            (SqlStarExpression, SqlStarExpression) => true,
            // The child comparison below also requires the same number of terms.
            (SqlLogicalExpression a, SqlLogicalExpression b) => a.Operator == b.Operator,
            (SqlBinaryExpression a, SqlBinaryExpression b) => a.Operator == b.Operator,
            (SqlUnaryExpression a, SqlUnaryExpression b) => a.Operator == b.Operator,
            (SqlFunctionCallExpression a, SqlFunctionCallExpression b) => a.FunctionName.Equals(b.FunctionName, StringComparison.OrdinalIgnoreCase)
                && (a.Quantifier == SqlSetQuantifier.Distinct) == (b.Quantifier == SqlSetQuantifier.Distinct)
                && a.HasWithinGroupOrdering == b.HasWithinGroupOrdering
                && (a.Filter is null) == (b.Filter is null)
                && a.Arguments.Count == b.Arguments.Count && a.OrderBy.Count == b.OrderBy.Count
                && a.OrderBy.Select(key => (key.IsDescending, key.NullPlacement)).SequenceEqual(b.OrderBy.Select(key => (key.IsDescending, key.NullPlacement))),
            (SqlCollateExpression a, SqlCollateExpression b) => Collation.FromName(a.CollationName) == Collation.FromName(b.CollationName),
            (SqlCastExpression a, SqlCastExpression b) => a.TargetTypeInfo?.Type == b.TargetTypeInfo?.Type
                && a.TargetTypeInfo?.MaxLength == b.TargetTypeInfo?.MaxLength
                && a.TargetTypeInfo?.Precision == b.TargetTypeInfo?.Precision && a.TargetTypeInfo?.Scale == b.TargetTypeInfo?.Scale
                // A domain target is not a built-in type the parser resolved: the same domain is the same
                // name as the catalog resolves it, ignoring case and quotes.
                && (a.TargetTypeInfo is not null || SqlTypeRules.SameTypeName(a.TargetType, b.TargetType)),
            (SqlDistinctExpression a, SqlDistinctExpression b) => a.IsNegated == b.IsNegated,
            (SqlBooleanTestExpression a, SqlBooleanTestExpression b) => a.IsNegated == b.IsNegated && a.TruthValue == b.TruthValue,
            (SqlIsJsonExpression a, SqlIsJsonExpression b) => a.IsNegated == b.IsNegated && a.Kind == b.Kind,
            (SqlIsNullExpression a, SqlIsNullExpression b) => a.IsNegated == b.IsNegated,
            (SqlBetweenExpression a, SqlBetweenExpression b) => a.IsNegated == b.IsNegated,
            (SqlInExpression a, SqlInExpression b) => a.IsNegated == b.IsNegated &&
                SqlPersistedExpression.SameQuery(a.Subquery, b.Subquery),
            (SqlLikeExpression a, SqlLikeExpression b) => a.IsNegated == b.IsNegated && a.IsCaseInsensitive == b.IsCaseInsensitive,
            (SqlCaseExpression a, SqlCaseExpression b) => (a.Input is null) == (b.Input is null)
                && (a.ElseResult is null) == (b.ElseResult is null) && a.WhenClauses.Count == b.WhenClauses.Count,
            _ => false,
        };
        return same && Children(left).SequenceEqual(Children(right), new GroupExpressionComparer(this, evaluator));

        bool SameColumn(SqlColumnReferenceExpression a, SqlColumnReferenceExpression b)
        {
            var first = evaluator.Bind(a);
            var second = evaluator.Bind(b);
            return first is SqlBoundColumn x && second is SqlBoundColumn y
                ? x.Ordinal == y.Ordinal && evaluator.ColumnMetadata(a).Type.Type == evaluator.ColumnMetadata(b).Type.Type
                : first is SqlBoundOuterColumn outerA && second is SqlBoundOuterColumn outerB
                    ? outerA.Reference.Scope == outerB.Reference.Scope &&
                        ((outerA.Reference.Value, outerB.Reference.Value) switch
                        {
                            (SqlBoundColumn leftColumn, SqlBoundColumn rightColumn) => leftColumn.Ordinal == rightColumn.Ordinal,
                            (SqlBoundSlot leftSlot, SqlBoundSlot rightSlot) => leftSlot.Ordinal == rightSlot.Ordinal,
                            _ => false,
                        }) && outerA.Reference.Column.Type.Type == outerB.Reference.Column.Type.Type
                    : ReferenceEquals(first, second);
        }
    }

    /// <summary>Structural equality is used for binding only, without expression hashing.</summary>
    [global::System.Diagnostics.DebuggerDisplay("GroupExpressionComparer")]
    [global::System.Diagnostics.DebuggerTypeProxy(typeof(global::Assimalign.Cohesion.Database.Sql.Internal.SqlPlanner.GroupExpressionComparer.DebugView))]
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

        private sealed class DebugView
        {
            public DebugView(global::Assimalign.Cohesion.Database.Sql.Internal.SqlPlanner.GroupExpressionComparer value)
            {
                _ = value;
            }
        }
    }

    /// <summary>Declares aggregate result types even when no source rows exist.</summary>
    private DatabaseType GroupExpressionType(SqlExpression expression, IReadOnlyList<SqlCatalogColumn> columns,
        SqlExpressionEvaluator evaluator)
    {
        RuntimeHelpers.EnsureSufficientExecutionStack();
        return evaluator.IsBoundValue(expression) ? evaluator.StaticTypeOf(expression) : GroupExpressionTypeCore(expression, columns, evaluator);
    }

    private DatabaseType GroupExpressionTypeCore(SqlExpression expression, IReadOnlyList<SqlCatalogColumn> columns,
        SqlExpressionEvaluator evaluator) => expression switch
    {
        SqlWindowExpression window => evaluator.WindowType(window),
        SqlColumnReferenceExpression column => evaluator.ColumnMetadata(column).Type.Type,
        SqlSubqueryExpression or SqlExistsExpression or SqlQuantifiedComparisonExpression or SqlInExpression { Subquery: not null }
            when _subqueryTypes.TryGetValue(expression, out var subqueryType) => subqueryType,
        SqlCollateExpression collate => GroupExpressionType(collate.Operand, columns, evaluator),
        SqlCastExpression cast => cast.TargetTypeInfo?.Type ?? ResolveCastTarget(cast, _functions.Types).Storage.Type,
        SqlParameterExpression parameter => GroupValueType(evaluator.Evaluate(parameter, [])),
        SqlLiteralExpression literal => literal.LiteralType switch
        {
            SqlLiteralType.Integer => DatabaseType.Int64,
            SqlLiteralType.Float => DatabaseType.Decimal,
            SqlLiteralType.String => DatabaseType.String,
            SqlLiteralType.Boolean => DatabaseType.Boolean,
            SqlLiteralType.Date => DatabaseType.Date,
            SqlLiteralType.Time => DatabaseType.Time,
            SqlLiteralType.Timestamp => DatabaseType.DateTime,
            SqlLiteralType.TimestampWithTimeZone => DatabaseType.DateTimeOffset,
            SqlLiteralType.Interval => DatabaseType.TimeSpan,
            SqlLiteralType.Binary => DatabaseType.Binary,
            _ => DatabaseType.Null,
        },
        SqlNextValueExpression => DatabaseType.Int64,
        SqlFunctionCallExpression call when SqlSequenceExecution.IsFunction(call.FunctionName) => DatabaseType.Int64,
        SqlFunctionCallExpression call when SqlStandardLibrary.IsCoalesce(call.FunctionName)
            => CoalesceGroupType(call.Arguments, columns, evaluator),
        SqlFunctionCallExpression call when call.FunctionName.Equals("NULLIF", StringComparison.OrdinalIgnoreCase)
            => GroupExpressionType(call.Arguments[0], columns, evaluator),
        SqlFunctionCallExpression call when SqlStandardLibrary.IsEvaluatorForm(call.FunctionName) => evaluator.StaticCallType(call),
        SqlFunctionCallExpression call when IsGrouping(call) => DatabaseType.Int32,
        SqlFunctionCallExpression call => CallResultType(call, columns, evaluator),
        SqlUnaryExpression { Operator: SqlUnaryOperator.Not } => DatabaseType.Boolean,
        // Unary plus returns its operand unchanged; only negation widens exact integers.
        SqlUnaryExpression { Operator: SqlUnaryOperator.Plus } plus => GroupExpressionType(plus.Operand, columns, evaluator),
        SqlUnaryExpression { Operator: SqlUnaryOperator.BitwiseNot } bitwise => evaluator.StaticTypeOf(bitwise),
        SqlUnaryExpression unary => GroupExpressionType(unary.Operand, columns, evaluator) switch
        {
            DatabaseType.Int8 or DatabaseType.Int16 or DatabaseType.Int32 or DatabaseType.Int64 => DatabaseType.Int64,
            DatabaseType.Float32 => DatabaseType.Float64,
            var type => type,
        },
        SqlBinaryExpression binary when binary.Operator == SqlBinaryOperator.Concat => DatabaseType.String,
        SqlBinaryExpression binary when binary.Operator is SqlBinaryOperator.Add or SqlBinaryOperator.Subtract
            or SqlBinaryOperator.Multiply or SqlBinaryOperator.Divide or SqlBinaryOperator.Modulo
            => SqlExpressionEvaluator.ArithmeticType(binary.Operator, GroupExpressionType(binary.Left, columns, evaluator), GroupExpressionType(binary.Right, columns, evaluator)),
        SqlBinaryExpression json when SqlJsonAccess.IsOperator(json.Operator)
            => SqlJsonAccess.ResultType(json.Operator, GroupExpressionType(json.Left, columns, evaluator)),
        SqlBinaryExpression bitwise when SqlBitwise.IsBinary(bitwise.Operator)
            => evaluator.StaticTypeOf(bitwise),
        SqlLogicalExpression or SqlBinaryExpression or SqlDistinctExpression or SqlBooleanTestExpression or SqlIsJsonExpression or SqlIsNullExpression or SqlBetweenExpression or SqlInExpression or SqlQuantifiedComparisonExpression
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
        if (left is DatabaseType.Character or DatabaseType.String && right is DatabaseType.Character or DatabaseType.String) { return DatabaseType.String; }
        if (SqlTemporalArithmetic.IsCalendar(left) && SqlTemporalArithmetic.IsCalendar(right)) { return SqlTemporalArithmetic.CommonCalendarType(left, right); }
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
        throw new SqlPlanningException("CS05017", string.Format(System.Globalization.CultureInfo.CurrentCulture, ErrorMessages.SqlPlannerCommonGroupTypeSqlPlanning, left, right));
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

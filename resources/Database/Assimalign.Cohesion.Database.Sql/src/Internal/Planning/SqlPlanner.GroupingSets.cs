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
    private SqlGroupPlan PlanGroupingSets(SqlSelectExpression select, SqlCatalogTable? table,
        SqlSystemViewDefinition? view, IReadOnlyList<SqlCatalogColumn> columns,
        IReadOnlyList<SqlRelationBinding>? bindings, SqlExpressionEvaluator evaluator, IReadOnlyList<SqlJoinStep> steps, SqlJoinTree? tree = null)
    {
        var (groupKeys, groupingSets) = ExpandGroupingSets(select, evaluator);
        List<(SqlFunctionCallExpression Call, int[] Arguments)>? groupingCalls = null;
        List<SqlBoundGrouping>? outerCalls = null;
        if (_queryScopes?.GroupingCalls?.TryGetValue(select, out outerCalls) == true && outerCalls.Count > 0)
        { groupingCalls = outerCalls.Select(call => (call.Call, call.Arguments)).ToList(); }
        foreach (var key in groupKeys)
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
                IsNullable = !(column.Expression is SqlFunctionCallExpression grouping && IsGrouping(grouping)) &&
                    !(column.Expression is SqlFunctionCallExpression projected && IsAggregate(projected)
                    && aggregates[slots[projected] - groupKeys.Count].Function.IsNeverNull),
            });
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

        if (groupingCalls is not null)
        {
            for (int i = 0; i < groupingCalls.Count; i++)
            { slots[groupingCalls[i].Call] = groupKeys.Count + aggregates.Count + i; }
            if (outerCalls is not null)
            {
                for (int i = 0; i < outerCalls.Count; i++)
                { outerCalls[i].Ordinal = groupKeys.Count + aggregates.Count + i; }
            }
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

        var keys = new SqlBoundKey[groupKeys.Count];
        for (int i = 0; i < keys.Length; i++)
        {
            var value = sourceScope.Bind(groupKeys[i], out SqlBoundCollation collation);
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
            groupScope.ForOrdering(projections, orderByProjections, keys.Length + boundAggregates.Length + (groupingCalls?.Count ?? 0)));

        return new SqlGroupingSetsPlan(new SqlGroupPlan(input, columns, bindings, keys, boundAggregates, projections,
            having, ordering, BindCount(select.Limit, LimitClause(select)), BindCount(select.Offset, "OFFSET"), select.IsDistinct),
            groupingSets, groupingCalls?.Select(item => item.Arguments).ToArray());

        void BindSlots(SqlExpression expression, IReadOnlyDictionary<SqlExpression, int>? outputSlots = null)
        {
            RuntimeHelpers.EnsureSufficientExecutionStack();
            if (outputSlots is not null && outputSlots.ContainsKey(expression))
            {
                return;
            }
            if (expression is SqlFunctionCallExpression grouping && IsGrouping(grouping))
            {
                if (evaluator.OuterGrouping(grouping) is not null) { return; }
                var arguments = new int[grouping.Arguments.Count];
                for (int i = 0; i < arguments.Length; i++)
                {
                    var argument = grouping.Arguments[i];
                    int index = -1;
                    if (!ContainsGrouping(argument) && !ContainsAggregate(argument))
                    {
                        ValidateExpression(argument, evaluator, _subqueryTypes);
                        if (argument is SqlColumnReferenceExpression column && evaluator.IsOuterColumn(column))
                        { throw new SqlPlanningException("CS01062", ErrorMessages.GroupingArgument, argument.Location); }
                        for (int k = 0; k < groupKeys.Count; k++)
                        { if (SameGroupExpression(argument, groupKeys[k], evaluator)) { index = k; break; } }
                    }
                    if (index < 0)
                    { throw new SqlPlanningException("CS01062", ErrorMessages.GroupingArgument, argument.Location); }
                    arguments[i] = index;
                }
                (groupingCalls ??= []).Add((grouping, arguments));
                slots[grouping] = -1; // final position follows all aggregate slots
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
                bool volatileInputs = Children(call).Any(HasVolatileInput);
                int index = volatileInputs ? -1 : aggregates.FindIndex(candidate => SameGroupExpression(candidate.Call, call, evaluator));
                if (index < 0)
                {
                    index = aggregates.Count;
                    aggregates.Add((call, function));
                }
                slots[expression] = groupKeys.Count + index;
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
            for (int i = 0; i < groupKeys.Count; i++)
            {
                // An alias-bearing expression is evaluated over completed outputs;
                // comparing it against source keys would rebind the alias as a column.
                if (!ContainsOutput(expression) && SameGroupExpression(expression, groupKeys[i], evaluator))
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
            foreach (var child in Children(expression))
            {
                BindSlots(child, outputSlots);
            }
            if (groupingCalls is null) { ValidateExpression(expression, evaluator, _subqueryTypes, outputSlots); }
            else if (outputSlots is null) { ValidateExpression(expression, evaluator, _subqueryTypes, slots); }
            else
            {
                var validationSlots = new Dictionary<SqlExpression, int>(slots);
                foreach (var output in outputSlots) { validationSlots.TryAdd(output.Key, output.Value); }
                ValidateExpression(expression, evaluator, _subqueryTypes, validationSlots);
            }

            bool ContainsOutput(SqlExpression candidate)
            {
                RuntimeHelpers.EnsureSufficientExecutionStack();
                return outputSlots is not null && (outputSlots.ContainsKey(candidate) || Children(candidate).Any(ContainsOutput));
            }
        }
    }

    private static bool IsGrouping(SqlFunctionCallExpression call) => SqlStandardLibrary.IsGrouping(call.FunctionName);

    private void BindOuterGroupingCalls(SqlSelectExpression select)
    {
        foreach (var expression in SubquerySources(select))
        {
            if (expression is not SqlFunctionCallExpression call || !IsGrouping(call)) { continue; }
            int level = AggregateLevel(call, new List<SqlSelectExpression?> { select });
            if (level <= 0) { continue; }
            var owner = OuterScope!;
            for (int i = 1; i < level; i++) { owner = owner.Parent!; }
            // parse_agg.c:348-358 chooses the argument level; the correlation scope
            // also records whether that level is currently producing grouped output.
            if (owner.GroupBy is null)
            { throw new SqlPlanningException("CS05074", ErrorMessages.GroupingPlacement, call.Location); }
            var arguments = new int[call.Arguments.Count];
            for (int i = 0; i < arguments.Length; i++)
            {
                var argument = call.Arguments[i];
                int key = -1;
                if (!ContainsGrouping(argument) && !ContainsAggregate(argument) &&
                    (argument is not SqlColumnReferenceExpression ||
                        AggregateLevel(call, new List<SqlSelectExpression?> { select }, argument) == level))
                {
                    for (int k = 0; k < owner.GroupBy.Count; k++)
                    {
                        if (SameGroupExpression(argument, owner.GroupBy[k], owner.Local)) { key = k; break; }
                    }
                }
                if (key < 0) { throw new SqlPlanningException("CS01062", ErrorMessages.GroupingArgument, argument.Location); }
                arguments[i] = key;
            }
            owner.GroupingCalls!.Add(new SqlBoundGrouping(call, owner.Id, arguments));
            OuterDependencies!.Add(owner.Id);
        }
    }

    private static IEnumerable<SqlExpression> GroupingKeys(IEnumerable<SqlExpression> elements)
    {
        RuntimeHelpers.EnsureSufficientExecutionStack();
        foreach (var element in elements)
        {
            if (element is SqlGroupingSetExpression group)
            {
                foreach (var key in GroupingKeys(group.Items)) { yield return key; }
            }
            else { yield return element; }
        }
    }

    private (IReadOnlyList<SqlExpression> Keys, int[][]? Sets) ExpandGroupingSets(
        SqlSelectExpression select, SqlExpressionEvaluator evaluator)
    {
        if (!select.GroupBy.Any(element => element is SqlGroupingSetExpression)) { return (select.GroupBy, null); }
        foreach (var element in select.GroupBy) { ValidateNesting(element, false); }
        var keys = new List<SqlExpression>();
        foreach (var expression in GroupingKeys(select.GroupBy))
        {
            if (ContainsAggregate(expression))
            { throw new SqlPlanningException("CS05017", ErrorMessages.SqlPlannerPlanGroupSqlPlanning, expression.Location); }
            if (ContainsGrouping(expression))
            { throw new SqlPlanningException("CS05074", ErrorMessages.GroupingPlacement, expression.Location); }
            ValidateExpression(expression, evaluator, _subqueryTypes);
            if (!keys.Any(key => SameGroupExpression(key, expression, evaluator))) { keys.Add(expression); }
        }

        // PostgreSQL parse_agg.c:1998-2102: product, per-set duplicate columns,
        // then optional duplicate-set elimination. The limit applies before DISTINCT.
        var sets = Product(select.GroupBy);
        sets.Sort((a, b) => a.Length.CompareTo(b.Length));
        if (select.IsGroupByDistinct)
        {
            sets.Sort(CompareSets);
            for (int i = sets.Count - 1; i > 0; i--)
            { if (sets[i].AsSpan().SequenceEqual(sets[i - 1])) { sets.RemoveAt(i); } }
        }
        return (keys, sets.ToArray());

        static void ValidateNesting(SqlExpression expression, bool withinRollupOrCube)
        {
            RuntimeHelpers.EnsureSufficientExecutionStack();
            if (expression is not SqlGroupingSetExpression group) { return; }
            // parse_clause.c:2225-2235: composites and nested SETS flatten, but
            // SETS may contain ROLLUP/CUBE only in this direction.
            if (withinRollupOrCube && group.Kind == SqlGroupingSetKind.Sets)
            { throw new SqlPlanningException("CS00010", ErrorMessages.GroupingSetsNesting, group.Location); }
            bool restricted = withinRollupOrCube || group.Kind is SqlGroupingSetKind.Rollup or SqlGroupingSetKind.Cube;
            foreach (var item in group.Items) { ValidateNesting(item, restricted); }
        }

        List<int[]> Expand(SqlExpression expression)
        {
            RuntimeHelpers.EnsureSufficientExecutionStack();
            _cancellationToken.ThrowIfCancellationRequested();
            if (expression is not SqlGroupingSetExpression group)
            { return [ [ keys.FindIndex(key => SameGroupExpression(key, expression, evaluator)) ] ]; }
            if (group.Kind == SqlGroupingSetKind.Composite) { return Product(group.Items); }
            var result = new List<int[]>();
            if (group.Kind == SqlGroupingSetKind.Sets)
            {
                foreach (var item in group.Items) { Append(result, Expand(item)); }
            }
            else if (group.Kind == SqlGroupingSetKind.Rollup)
            {
                for (int size = group.Items.Count; size >= 0; size--)
                { Append(result, Product(group.Items.Take(size))); }
            }
            else
            {
                result.Add([]);
                foreach (var item in group.Items)
                {
                    var included = Multiply(result, Expand(item));
                    Append(result, included);
                }
            }
            return result;
        }

        List<int[]> Product(IEnumerable<SqlExpression> expressions)
        {
            var result = new List<int[]> { Array.Empty<int>() };
            foreach (var expression in expressions) { result = Multiply(result, Expand(expression)); }
            return result;
        }

        List<int[]> Multiply(List<int[]> left, List<int[]> right)
        {
            Check((long)left.Count * right.Count);
            var result = new List<int[]>(left.Count * right.Count);
            foreach (var a in left)
            {
                foreach (var b in right)
                { result.Add(a.Concat(b).Distinct().Order().ToArray()); }
            }
            return result;
        }

        void Append(List<int[]> result, List<int[]> addition)
        { Check((long)result.Count + addition.Count); result.AddRange(addition); }

        void Check(long count)
        {
            if (count > 4096)
            { throw new SqlPlanningException("CS05075", ErrorMessages.GroupingExpansionLimit, select.GroupBy[0].Location); }
        }

        static int CompareSets(int[] a, int[] b)
        {
            int comparison = a.Length.CompareTo(b.Length);
            for (int i = 0; comparison == 0 && i < a.Length; i++) { comparison = a[i].CompareTo(b[i]); }
            return comparison;
        }
    }

    // This extra dispatch walk must allocate nothing for ordinary GROUP BY.
    private static bool ContainsGrouping(SqlExpression expression)
    {
        RuntimeHelpers.EnsureSufficientExecutionStack();
        switch (expression)
        {
            case SqlFunctionCallExpression call:
                if (IsGrouping(call)) { return true; }
                for (int i = 0; i < call.Arguments.Count; i++) { if (ContainsGrouping(call.Arguments[i])) { return true; } }
                for (int i = 0; i < call.OrderBy.Count; i++) { if (ContainsGrouping(call.OrderBy[i].Expression)) { return true; } }
                return call.Filter is not null && ContainsGrouping(call.Filter);
            case SqlLogicalExpression logical:
                for (int i = 0; i < logical.Operands.Count; i++) { if (ContainsGrouping(logical.Operands[i])) { return true; } }
                return false;
            case SqlBinaryExpression binary: return ContainsGrouping(binary.Left) || ContainsGrouping(binary.Right);
            case SqlUnaryExpression unary: return ContainsGrouping(unary.Operand);
            case SqlCollateExpression collate: return ContainsGrouping(collate.Operand);
            case SqlCastExpression cast: return ContainsGrouping(cast.Operand);
            case SqlDistinctExpression distinct: return ContainsGrouping(distinct.Left) || ContainsGrouping(distinct.Right);
            case SqlBooleanTestExpression test: return ContainsGrouping(test.Operand);
            case SqlIsNullExpression test: return ContainsGrouping(test.Operand);
            case SqlBetweenExpression between: return ContainsGrouping(between.Operand) || ContainsGrouping(between.Low) || ContainsGrouping(between.High);
            case SqlLikeExpression like: return ContainsGrouping(like.Operand) || ContainsGrouping(like.Pattern) || like.Escape is not null && ContainsGrouping(like.Escape);
            case SqlQuantifiedComparisonExpression quantified: return ContainsGrouping(quantified.Operand);
            case SqlInExpression member:
                if (ContainsGrouping(member.Operand)) { return true; }
                for (int i = 0; i < (member.Values?.Count ?? 0); i++) { if (ContainsGrouping(member.Values![i])) { return true; } }
                return false;
            case SqlCaseExpression conditional:
                if (conditional.Input is not null && ContainsGrouping(conditional.Input)) { return true; }
                for (int i = 0; i < conditional.WhenClauses.Count; i++)
                { if (ContainsGrouping(conditional.WhenClauses[i].Condition) || ContainsGrouping(conditional.WhenClauses[i].Result)) { return true; } }
                return conditional.ElseResult is not null && ContainsGrouping(conditional.ElseResult);
            default: return false;
        }
    }
}

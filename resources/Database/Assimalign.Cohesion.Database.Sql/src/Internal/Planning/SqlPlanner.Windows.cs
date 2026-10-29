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
    internal static bool IsWindowFunction(string name) =>
        name.Equals("ROW_NUMBER", StringComparison.OrdinalIgnoreCase) || name.Equals("RANK", StringComparison.OrdinalIgnoreCase) ||
        name.Equals("DENSE_RANK", StringComparison.OrdinalIgnoreCase) || name.Equals("PERCENT_RANK", StringComparison.OrdinalIgnoreCase) ||
        name.Equals("CUME_DIST", StringComparison.OrdinalIgnoreCase) || name.Equals("NTILE", StringComparison.OrdinalIgnoreCase) ||
        name.Equals("LAG", StringComparison.OrdinalIgnoreCase) || name.Equals("LEAD", StringComparison.OrdinalIgnoreCase) ||
        name.Equals("FIRST_VALUE", StringComparison.OrdinalIgnoreCase) || name.Equals("LAST_VALUE", StringComparison.OrdinalIgnoreCase) ||
        name.Equals("NTH_VALUE", StringComparison.OrdinalIgnoreCase);

    private static bool ContainsWindow(SqlExpression expression)
    {
        RuntimeHelpers.EnsureSufficientExecutionStack();
        // This probe runs on window-free queries too. Walk operands directly so the
        // existing parser/planner allocation guards do not pay for iterator objects.
        return expression switch
        {
            SqlWindowExpression => true,
            SqlBinaryExpression x => ContainsWindow(x.Left) || ContainsWindow(x.Right),
            SqlUnaryExpression x => ContainsWindow(x.Operand),
            SqlCastExpression x => ContainsWindow(x.Operand),
            SqlCollateExpression x => ContainsWindow(x.Operand),
            SqlDistinctExpression x => ContainsWindow(x.Left) || ContainsWindow(x.Right),
            SqlBooleanTestExpression x => ContainsWindow(x.Operand),
            SqlIsNullExpression x => ContainsWindow(x.Operand),
            SqlBetweenExpression x => ContainsWindow(x.Operand) || ContainsWindow(x.Low) || ContainsWindow(x.High),
            SqlLikeExpression x => ContainsWindow(x.Operand) || ContainsWindow(x.Pattern) || x.Escape is not null && ContainsWindow(x.Escape),
            SqlQuantifiedComparisonExpression x => ContainsWindow(x.Operand),
            SqlInExpression x => ContainsWindow(x.Operand) || Any(x.Values),
            SqlLogicalExpression x => Any(x.Operands),
            SqlFunctionCallExpression x => Any(x.Arguments) || AnyOrder(x.OrderBy) || x.Filter is not null && ContainsWindow(x.Filter),
            SqlCaseExpression x => CaseWindow(x),
            SqlGroupingSetExpression x => Any(x.Items),
            _ => false,
        };

        static bool Any(IReadOnlyList<SqlExpression>? values)
        {
            if (values is not null)
            { for (int i = 0; i < values.Count; i++) { if (ContainsWindow(values[i])) { return true; } } }
            return false;
        }
        static bool AnyOrder(IReadOnlyList<SqlOrderByColumn> values)
        { for (int i = 0; i < values.Count; i++) { if (ContainsWindow(values[i].Expression)) { return true; } } return false; }
        static bool CaseWindow(SqlCaseExpression value)
        {
            if (value.Input is not null && ContainsWindow(value.Input) || value.ElseResult is not null && ContainsWindow(value.ElseResult)) { return true; }
            for (int i = 0; i < value.WhenClauses.Count; i++)
            { if (ContainsWindow(value.WhenClauses[i].Condition) || ContainsWindow(value.WhenClauses[i].Result)) { return true; } }
            return false;
        }
    }

    private static IEnumerable<SqlWindowExpression> WindowExpressions(SqlSelectExpression select)
        => select.Columns.Select(column => column.Expression).Concat(select.OrderBy.Select(key => key.Expression)).SelectMany(WalkWindows);

    private static IEnumerable<SqlWindowExpression> WalkWindows(SqlExpression expression)
    {
        RuntimeHelpers.EnsureSufficientExecutionStack();
        if (expression is SqlWindowExpression window) { yield return window; yield break; }
        foreach (var child in Children(expression))
        { foreach (var result in WalkWindows(child)) { yield return result; } }
    }

    private static bool HasWindows(SqlSelectExpression select)
    {
        for (int i = 0; i < select.Columns.Count; i++) { if (ContainsWindow(select.Columns[i].Expression)) { return true; } }
        for (int i = 0; i < select.OrderBy.Count; i++) { if (ContainsWindow(select.OrderBy[i].Expression)) { return true; } }
        return false;
    }

    private sealed record WindowKeys(IReadOnlyList<SqlExpression> Partition, IReadOnlyList<SqlOrderByColumn> Order, SqlWindowFrame? Frame);

    private static Dictionary<string, WindowKeys> NamedWindows(SqlSelectExpression select)
    {
        var names = new Dictionary<string, WindowKeys>(StringComparer.OrdinalIgnoreCase);
        foreach (var window in select.Windows)
        {
            if (names.ContainsKey(window.Name))
            { throw new SqlPlanningException("CS01064", ErrorMessages.WindowDuplicateName, select.Location); }
            ValidateFrameBounds(window.Specification.Frame);
            names.Add(window.Name, WindowKeysOf(window.Specification, names));
        }
        return names;
    }

    private static WindowKeys WindowKeysOf(SqlWindowSpecification specification, Dictionary<string, WindowKeys> names)
    {
        if (specification.ExistingWindow is not { } name) { return new(specification.PartitionBy, specification.OrderBy, specification.Frame); }
        if (!names.TryGetValue(name, out var parent))
        { throw new SqlPlanningException("CS01065", ErrorMessages.WindowUnknownName); }
        if (parent.Frame is not null || specification.PartitionBy.Count > 0 || specification.OrderBy.Count > 0 && parent.Order.Count > 0)
        { throw new SqlPlanningException("CS01066", ErrorMessages.WindowInvalidCopy); }
        return new(parent.Partition, specification.OrderBy.Count == 0 ? parent.Order : specification.OrderBy, specification.Frame);
    }

    private static WindowKeys WindowKeysOf(SqlWindowExpression window, Dictionary<string, WindowKeys> names)
    {
        if (window.Specification is { } specification) { return WindowKeysOf(specification, names); }
        if (window.WindowName is { } name && names.TryGetValue(name, out var keys)) { return keys; }
        throw new SqlPlanningException("CS01065", ErrorMessages.WindowUnknownName, window.Location);
    }

    private void ValidateWindowDefinitions(SqlSelectExpression select, SqlExpressionEvaluator scope)
    {
        if (select.Windows.Count == 0) { return; }
        var names = NamedWindows(select);
        foreach (var definition in select.Windows)
        {
            var keys = WindowKeysOf(definition.Specification, names);
            _ = BindFrame(keys.Frame, scope, keys.Order);
            foreach (var key in definition.Specification.PartitionBy) { ValidateExpression(key, scope, _subqueryTypes); }
            foreach (var key in definition.Specification.OrderBy) { ValidateExpression(key.Expression, scope, _subqueryTypes); }
        }
    }

    private void ValidateNamedWindows(SqlSelectExpression select)
    {
        if (select.Windows.Count > 0) { _ = NamedWindows(select); }
    }

    private List<SqlWindowGroup> BindWindows(SqlSelectExpression select, SqlExpressionEvaluator scope,
        Dictionary<SqlExpression, int> slots, int start)
    {
        var names = NamedWindows(select);
        var groups = new List<SqlWindowGroup>();
        var frames = new Dictionary<SqlWindowFrame, SqlWindowFrameBinding>();
        foreach (var window in WindowExpressions(select).Distinct())
        {
            var keys = WindowKeysOf(window, names);
            foreach (var key in keys.Partition) { ValidateExpression(key, scope, _subqueryTypes); }
            foreach (var key in keys.Order) { ValidateExpression(key.Expression, scope, _subqueryTypes); }
            SqlWindowFrameBinding? frame = null;
            if (keys.Frame is { } syntax && !frames.TryGetValue(syntax, out frame))
            { frames.Add(syntax, frame = BindFrame(syntax, scope, keys.Order)!); }
            var call = window.Function;
            string name = call.FunctionName.ToUpperInvariant();
            int count = call.Arguments.Count;
            if (window.NullTreatment != SqlWindowNullTreatment.Default && name is not ("LAG" or "LEAD" or "FIRST_VALUE" or "LAST_VALUE" or "NTH_VALUE"))
            { throw new SqlPlanningException("CS05088", ErrorMessages.WindowNullTreatment, window.Location); }
            SqlGroupAggregate? aggregate = null;
            if (_functions.Catalog.IsAggregate(name)) { aggregate = BindWindowAggregate(call, scope); }
            else
            {
                if (!IsWindowFunction(name))
                { CheckCall(call, _functions.Catalog); throw new SqlPlanningException("CS05083", ErrorMessages.WindowRequiresAggregate, window.Location); }
                if (call.Quantifier != SqlSetQuantifier.Default || call.OrderBy.Count > 0 || call.Filter is not null ||
                    call.Arguments.Any(argument => argument is SqlStarExpression) ||
                    (name is "LAG" or "LEAD" ? count < 1 || count > 3 : name is "NTILE" or "FIRST_VALUE" or "LAST_VALUE" ? count != 1 : name == "NTH_VALUE" ? count != 2 : count != 0))
                { throw new SqlPlanningException("CS00011", ErrorMessages.WindowSignature, window.Location); }
                foreach (var argument in call.Arguments) { ValidateExpression(argument, scope, _subqueryTypes); }
            }
            int integerArgument = name == "NTILE" ? 0 : name == "NTH_VALUE" || name is "LAG" or "LEAD" && count > 1 ? 1 : -1;
            if (integerArgument >= 0 && scope.StaticTypeOf(window.Function.Arguments[integerArgument]) is not
                (DatabaseType.Null or DatabaseType.Int8 or DatabaseType.Int16 or DatabaseType.Int32 or DatabaseType.Int64))
            { throw new SqlPlanningException(name == "NTH_VALUE" ? "CS02073" : "CS02068",
                name == "NTH_VALUE" ? ErrorMessages.WindowNthInteger : ErrorMessages.WindowIntegerArgument, window.Location); }
            var group = groups.FirstOrDefault(candidate => SameKeys(candidate.PartitionSyntax, keys.Partition) && SameOrder(candidate.OrderSyntax, keys.Order));
            if (group is null)
            {
                var partition = keys.Partition.Select(key =>
                { var value = scope.Bind(key, out SqlBoundCollation collation); return new SqlBoundOrdering(value, false, true, collation); }).ToArray();
                group = new(keys.Partition, keys.Order, partition, BindOrdering(keys.Order, scope), []);
                groups.Add(group);
            }
            int ordinal = start++;
            slots[window] = ordinal;
            var type = scope.WindowType(window);
            var arguments = aggregate?.Arguments ?? window.Function.Arguments.Select(scope.Bind).ToArray();
            if (name is "LAG" or "LEAD")
            {
                Coerce(0);
                if (arguments.Length > 2) { Coerce(2); }
            }
            group.Functions.Add(new(window, name, ordinal, arguments, type, frame, aggregate));

            void Coerce(int index)
            {
                var source = scope.StaticTypeOf(window.Function.Arguments[index]);
                if (source == type || source == DatabaseType.Null) { return; }
                var target = SqlType.ForStorage(new DatabaseTypeInfo(type));
                var cast = new SqlBoundCast(arguments[index], target.Name, target, _functions.Types.ResolveCasts(null, target),
                    _functions.Database, _catalog.DefaultCollation, sourceType: source);
                arguments[index] = window.Function.Arguments[index] is SqlLiteralExpression { LiteralType: SqlLiteralType.String } literal
                    ? new SqlBoundConstant(SqlCastConverter.Convert(literal.Value, cast, _cancellationToken)) : cast;
            }
        }
        // Longer orderings first allow a later prefix to reuse the same sorted row index.
        groups.Sort((a, b) => (b.Partition.Count + b.Ordering.Count).CompareTo(a.Partition.Count + a.Ordering.Count));
        for (int i = 1; i < groups.Count; i++)
        {
            var previous = groups[i - 1];
            int compatible = groups.FindIndex(i, candidate => CanShare(previous, candidate));
            if (compatible > i)
            {
                var next = groups[compatible];
                groups.RemoveAt(compatible);
                groups.Insert(i, next);
            }
        }
        for (int i = 1; i < groups.Count; i++)
        {
            var current = groups[i]; var previous = groups[i - 1];
            current.SortShared = CanShare(previous, current);
        }
        return groups;

        bool CanShare(SqlWindowGroup previous, SqlWindowGroup current)
        {
            int length = current.Partition.Count + current.Ordering.Count;
            if (previous.Partition.Count + previous.Ordering.Count < length) { return false; }
            for (int i = 0; i < length; i++)
            {
                var a = Key(previous, i); var b = Key(current, i);
                if (a.Descending != b.Descending || a.NullsFirst != b.NullsFirst || !SameGroupExpression(a.Expression, b.Expression, scope)) { return false; }
            }
            return true;
        }

        static (SqlExpression Expression, bool Descending, bool NullsFirst) Key(SqlWindowGroup group, int index)
        {
            if (index < group.PartitionSyntax.Count) { return (group.PartitionSyntax[index], false, true); }
            var order = group.OrderSyntax[index - group.PartitionSyntax.Count];
            return (order.Expression, order.IsDescending, NullsFirst(order));
        }

        bool SameKeys(IReadOnlyList<SqlExpression> a, IReadOnlyList<SqlExpression> b) => a.Count == b.Count &&
            a.Select((key, i) => SameGroupExpression(key, b[i], scope)).All(same => same);
        bool SameOrder(IReadOnlyList<SqlOrderByColumn> a, IReadOnlyList<SqlOrderByColumn> b) => a.Count == b.Count &&
            a.Select((key, i) => key.IsDescending == b[i].IsDescending && NullsFirst(key) == NullsFirst(b[i]) &&
                SameGroupExpression(key.Expression, b[i].Expression, scope)).All(same => same);
    }

    private static bool IsFrameOffsetSource(SqlSelectExpression select, SqlExpression source)
    {
        if (select.Windows.Count == 0 && !HasWindows(select)) { return false; }
        foreach (var window in WindowExpressions(select))
        { if (Includes(window.Specification?.Frame)) { return true; } }
        foreach (var definition in select.Windows)
        { if (Includes(definition.Specification.Frame)) { return true; } }
        return false;

        bool Includes(SqlWindowFrame? frame) => frame is not null &&
            (frame.Start.Offset is { } start && WalkSubqueries(start).Any(node => ReferenceEquals(node, source)) ||
             frame.End.Offset is { } end && WalkSubqueries(end).Any(node => ReferenceEquals(node, source)));
    }

    private void ValidateFrameAggregateScope(SqlWindowFrame? frame)
    {
        if (frame?.Start.Offset is { } start && (ContainsAggregate(start) || ContainsGrouping(start)) ||
            frame?.End.Offset is { } end && (ContainsAggregate(end) || ContainsGrouping(end)))
        { throw new SqlPlanningException("CS05081", ErrorMessages.WindowFrameOffsetScope); }
    }

    private static void ValidateFrameBounds(SqlWindowFrame? frame)
    {
        if (frame is null) { return; }
        // PostgreSQL gram.y checks boundary categories, not the values of two offsets
        // in the same direction. ROWS BETWEEN 1 PRECEDING AND 2 PRECEDING is empty.
        if (frame.Start.Kind == SqlWindowFrameBoundKind.UnboundedFollowing ||
            frame.End.Kind == SqlWindowFrameBoundKind.UnboundedPreceding || frame.Start.Kind > frame.End.Kind)
        { throw new SqlPlanningException("CS05080", ErrorMessages.WindowFrameBounds); }
    }

    private SqlWindowFrameBinding? BindFrame(SqlWindowFrame? frame, SqlExpressionEvaluator scope, IReadOnlyList<SqlOrderByColumn> ordering)
    {
        ValidateFrameBounds(frame);
        if (frame is null) { return null; }
        bool rangeOffset = frame.Mode == SqlWindowFrameMode.Range && (frame.Start.Offset is not null || frame.End.Offset is not null);
        if (rangeOffset && ordering.Count != 1)
        { throw new SqlPlanningException("CS05086", ErrorMessages.WindowRangeOrdering); }
        if (frame.Mode == SqlWindowFrameMode.Groups && ordering.Count == 0)
        { throw new SqlPlanningException("CS05087", ErrorMessages.WindowGroupsOrdering); }
        var keyType = rangeOffset ? scope.StaticTypeOf(ordering[0].Expression) : DatabaseType.Null;
        if (rangeOffset && !SqlWindowRange.Supports(keyType))
        { throw new SqlPlanningException("CS02074", ErrorMessages.WindowRangeKeyType); }
        return new(frame, Offset(frame.Start.Offset), Offset(frame.End.Offset));

        SqlBoundExpression? Offset(SqlExpression? expression)
        {
            if (expression is null) { return null; }
            Check(expression);
            ValidateExpression(expression, scope, _subqueryTypes);
            var offsetType = scope.StaticTypeOf(expression);
            if (rangeOffset && !SqlWindowRange.AcceptsOffset(keyType, offsetType))
            { throw new SqlPlanningException("CS02075", ErrorMessages.WindowRangeOffsetType, expression.Location); }
            if (!rangeOffset && offsetType is not (DatabaseType.Null or DatabaseType.Int8 or DatabaseType.Int16 or DatabaseType.Int32 or DatabaseType.Int64))
            { throw new SqlPlanningException("CS02070", ErrorMessages.WindowFrameInteger, expression.Location); }
            return scope.Bind(expression);
        }
        void Check(SqlExpression expression)
        {
            RuntimeHelpers.EnsureSufficientExecutionStack();
            if (expression is SqlColumnReferenceExpression && scope.Bind(expression) is not SqlBoundOuterColumn || expression is SqlWindowExpression ||
                expression is SqlFunctionCallExpression call && (IsAggregate(call) || IsGrouping(call)))
            { throw new SqlPlanningException("CS05081", ErrorMessages.WindowFrameOffsetScope, expression.Location); }
            foreach (var child in Children(expression)) { Check(child); }
        }
    }

    private SqlGroupAggregate BindWindowAggregate(SqlFunctionCallExpression call, SqlExpressionEvaluator scope)
    {
        if (call.Quantifier == SqlSetQuantifier.Distinct)
        { throw new SqlUnsupportedQueryException(ErrorMessages.WindowDistinctAggregate, location: call.Location); }
        CheckCall(call, _functions.Catalog);
        foreach (var argument in Children(call))
        {
            if (argument is not SqlStarExpression) { ValidateExpression(argument, scope, _subqueryTypes); }
        }
        if (call.Filter is not null) { ValidatePredicate(call.Filter, scope, "FILTER", _subqueryTypes); }
        if (call.FunctionName.Equals("LISTAGG", StringComparison.OrdinalIgnoreCase) && (!call.HasWithinGroupOrdering || call.OrderBy.Count == 0))
        { throw new SqlPlanningException("CS00003", ErrorMessages.ListaggRequiresWithinGroup, call.Location); }
        var function = (SqlAggregateFunction)scope.ResolveFunction(call)!;
        SqlBoundExpression[] arguments;
        SqlBoundCollation collation;
        if (function is SqlStringAggregateFunction)
        {
            if (call.Arguments.Count > 1 && call.Arguments[1] is not
                (SqlLiteralExpression { LiteralType: SqlLiteralType.String or SqlLiteralType.Null } or SqlParameterExpression))
            { throw new SqlUnsupportedQueryException(ErrorMessages.StringAggregateDelimiter, location: call.Arguments[1].Location); }
            arguments = new SqlBoundExpression[call.Arguments.Count];
            arguments[0] = scope.Bind(call.Arguments[0], out collation);
            for (int i = 1; i < arguments.Length; i++) { arguments[i] = scope.Bind(call.Arguments[i]); }
        }
        else { arguments = scope.BindArguments(call.Arguments, out collation, statisticsInput: function is SqlStatisticsFunction); }
        scope.BindParameterCoercions(function, arguments, collation);
        return new(call, function, arguments, SqlFunctionResolver.CoercionTargets(function, arguments.Length), collation,
            _functions.Database, BindOrdering(call.OrderBy, scope), [], call.Filter is null ? null : scope.BindPredicate(call.Filter));
    }

    private SqlPlan PlanWindowSelect(SqlSelectExpression select, SqlCatalogTable? table, SqlSystemViewDefinition? view,
        IReadOnlyList<SqlCatalogColumn> columns, IReadOnlyList<SqlRelationBinding>? bindings, SqlExpressionEvaluator scope,
        IReadOnlyList<SqlJoinStep> steps, IReadOnlyList<int>? outputOrdinals = null, SqlJoinTree? tree = null)
    {
        foreach (var definition in select.Windows)
        {
            foreach (var key in definition.Specification.PartitionBy) { ValidateExpression(key, scope, _subqueryTypes); }
            foreach (var key in definition.Specification.OrderBy) { ValidateExpression(key.Expression, scope, _subqueryTypes); }
        }
        var slots = new Dictionary<SqlExpression, int>();
        var groups = BindWindows(select, scope, slots, columns.Count);
        var outputScope = new SqlExpressionEvaluator(columns, _parameters, bindings, slots, _catalog.DefaultCollation,
            subquerySlots: _subquerySlots, functions: _functions, cancellationToken: _cancellationToken, foldConstants: FoldQueryConstants,
            outerScope: OuterScope, outerDependencies: OuterDependencies, slotScope: scope,
            relation: select.From.Count == 0 ? null : select.From[0].Table);
        var projections = BindProjections(select.Columns, columns, outputScope, outputOrdinals);
        var orderSlots = BindOrderByProjections(select, projections, columns.Count);
        foreach (var order in select.OrderBy) { ValidateExpression(order.Expression, outputScope, _subqueryTypes, orderSlots); }
        CaptureViewOrdering(select, columns, bindings, outputScope, orderSlots);
        var ordering = BindOrdering(select.OrderBy, outputScope.ForOrdering(projections, orderSlots, columns.Count + slots.Count));
        if (select.Where is not null) { ValidatePredicate(select.Where, scope, "WHERE", _subqueryTypes); }
        var where = select.Where is null ? null : scope.BindPredicate(select.Where);
        var source = columns.Select((column, i) => PassThrough(column.Name, i, columns, scope)).ToArray();
        SqlPlan input = bindings is not null ? PushJoinPredicates(new SqlJoinPlan(bindings, columns, steps, source, where, [], null, null, false) { Tree = tree })
            : view is not null ? new SqlSystemViewPlan(view, source, where, [], null, null, false)
            : table is not null ? new SqlSelectPlan(table, source, where, [], null, null, false, SelectAccessPath(table, select.Where, OuterScope is null ? null : scope))
            : new SqlConstantPlan(source, where, [], null, null, false, null);
        return new SqlWindowAggregatePlan(input, columns, bindings, columns.Count, groups, projections, ordering,
            BindCount(select.Limit, LimitClause(select)), BindCount(select.Offset, "OFFSET"), select.IsDistinct, orderSlots);
    }

    private SqlPlan FinishWindowGroup(SqlSelectExpression select, SqlGroupPlan plan, SqlExpressionEvaluator scope,
        List<SqlWindowGroup> groups, int width, IReadOnlyDictionary<SqlExpression, int>? orderSlots)
    {
        var values = new SqlProjection[width];
        for (int i = 0; i < width; i++)
        {
            var syntax = i < plan.Keys.Count ? null : i < plan.Keys.Count + plan.Aggregates.Count ? plan.Aggregates[i - plan.Keys.Count].Call : null;
            // Internal group rows retain exact runtime values. Null metadata avoids normalization
            // before the enclosing stage applies each expression's declared result type.
            values[i] = new SqlProjection("slot" + i, null, syntax, DatabaseType.Null) { Value = new SqlBoundSlot(i) };
        }
        var input = plan with { Projections = values, OrderBy = [], Limit = null, Offset = null, IsDistinct = false };
        var ordering = BindOrdering(select.OrderBy, scope.ForOrdering(plan.Projections, orderSlots,
            width + groups.Sum(group => group.Functions.Count)));
        return new SqlWindowAggregatePlan(input, plan.SourceColumns, plan.Bindings, width, groups,
            plan.Projections, ordering, plan.Limit, plan.Offset, plan.IsDistinct, orderSlots);
    }
}

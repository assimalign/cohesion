using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using Assimalign.Cohesion.Database.Graph.Language;

namespace Assimalign.Cohesion.Database.Graph.Internal;

internal static class GraphGrouping
{
    internal static GraphGroupingPlan? Plan(GqlQueryExpression query, IReadOnlyList<GraphProjectionPlan> projections, CancellationToken token,
        Action<GqlExpression>? validateKey = null, GraphFunctionCatalog? functions = null)
    {
        var shape = new GraphExpressionShape(token);
        var aggregates = new HashSet<GqlExpression>(shape);
        var keys = new HashSet<GqlExpression>(shape);
        var aggregateList = new List<GqlExpression>();
        var keyList = new List<GqlExpression>();
        var candidates = new List<GqlExpression>();
        var groupingNames = new Dictionary<string, GqlExpression?>(StringComparer.Ordinal);
        foreach (var projection in projections)
        {
            bool aggregated = false;
            var pending = new Stack<(GqlExpression Expression, bool InsideAggregate)>();
            pending.Push((projection.Expression, false));
            while (pending.TryPop(out var item))
            {
                token.ThrowIfCancellationRequested();
                bool inside = item.InsideAggregate;
                if (item.Expression is GqlAggregateExpression || item.Expression is GqlNamedFunctionCallExpression named && functions?.Resolve(named) is GraphAggregateFunction)
                {
                    if (inside) { throw new GraphPlanningException("CG05001", ErrorMessages.AggregatePlacement, item.Expression.Location); }
                    aggregated = inside = true;
                    if (aggregates.Add(item.Expression)) { aggregateList.Add(item.Expression); }
                }
                foreach (var child in GraphValueExpressions.Children(item.Expression)) { pending.Push((child, inside)); }
            }
            if (!aggregated) { candidates.Add(projection.Expression); }
            groupingNames.Add(projection.Name, aggregated ? null : projection.Expression);
        }
        if (query.GroupBy is null && aggregateList.Count == 0) { return null; }
        foreach (var candidate in candidates)
        { token.ThrowIfCancellationRequested(); if (keys.Add(candidate)) { keyList.Add(candidate); } }
        if (projections.Count == 0) { throw new GraphPlanningException("CG05001", ErrorMessages.GroupByMismatch, query.Location); }
        if (query.GroupBy is not null)
        {
            var explicitKeys = new List<GqlExpression>();
            foreach (var key in query.GroupBy)
            {
                token.ThrowIfCancellationRequested();
                if (key is null) { throw new GraphPlanningException("CG05001", ErrorMessages.GroupByMismatch, query.Location); }
                GqlExpression resolved = key;
                // Neo4j AggregationChecker.scala:117-144 exempts non-aggregate grouping
                // aliases and diagnoses an aggregate alias. Expressions retain input scope.
                if (key is GqlVariableExpression name && groupingNames.TryGetValue(name.Variable, out var item))
                { resolved = item ?? throw new GraphPlanningException("CG05001", ErrorMessages.AggregatePlacement, key.Location); }
                validateKey?.Invoke(resolved);
                explicitKeys.Add(resolved);
            }
            if (explicitKeys.Count != keys.Count || !keys.SetEquals(explicitKeys))
            { throw new GraphPlanningException("CG05001", ErrorMessages.GroupByMismatch, query.Location); }
        }
        var scope = new GraphGroupingScope(keyList, aggregateList, shape, token, functions);
        var plan = new GraphGroupingPlan(keyList.AsReadOnly(), aggregateList.AsReadOnly(), scope);
        foreach (var projection in projections) { scope.Validate(projection.Expression); }
        return plan;
    }

    /// <summary>Consumes one borrowed binding at a time; groups retain keys and aggregate state only.</summary>
    [global::System.Diagnostics.DebuggerDisplay("Accumulator")]
    [global::System.Diagnostics.DebuggerTypeProxy(typeof(global::Assimalign.Cohesion.Database.Graph.Internal.GraphGrouping.Accumulator.DebugView))]
    internal sealed class Accumulator : IDisposable
    {
        private readonly GraphGroupingPlan _plan;
        private readonly GraphEvaluationContext _context;
        private readonly IReadOnlyDictionary<string, object?>? _parameters;
        private readonly CancellationToken _token;
        private readonly Dictionary<object?[], Group> _groups = new(GraphRowComparer.Instance);
        private readonly List<Group> _ordered = [];
        private readonly GraphMemoryOwner? _memory;

        internal Accumulator(GraphGroupingPlan plan, GraphEvaluationContext context,
            IReadOnlyDictionary<string, object?>? parameters, CancellationToken token, GraphMemoryOwner? memory = null)
        {
            _plan = plan;
            _context = context;
            _parameters = parameters;
            _token = token;
            _memory = memory;
            // An ungrouped aggregate has exactly one group even with no source rows.
            if (plan.Keys.Count == 0) { AddGroup([]); }
        }

        internal void Add(IReadOnlyDictionary<string, object?> binding)
        {
            _token.ThrowIfCancellationRequested();
            var key = new object?[_plan.Keys.Count];
            for (int i = 0; i < key.Length; i++)
            { _token.ThrowIfCancellationRequested(); key[i] = GraphExpressionEvaluator.Evaluate(_plan.Keys[i], binding, _context, _parameters); }
            if (!_groups.TryGetValue(key, out var group)) { group = AddGroup(key); }
            for (int i = 0; i < group.Accumulators.Length; i++)
            {
                _token.ThrowIfCancellationRequested();
                GqlExpression expression = _plan.Aggregates[i];
                int count = expression is GqlNamedFunctionCallExpression named ? named.Arguments.Count
                    : ((GqlAggregateExpression)expression).Operand is null ? 0 : 1;
                GraphInlineArguments buffer = default;
                Span<object?> values = count <= 3 ? ((Span<object?>)buffer)[..count] : new object?[count];
                for (int j = 0; j < values.Length; j++)
                {
                    GqlExpression input = expression is GqlNamedFunctionCallExpression call ? call.Arguments[j] : ((GqlAggregateExpression)expression).Operand!;
                    values[j] = GraphExpressionEvaluator.Evaluate(input, binding, _context, _parameters);
                }
                if (group.Distinct[i] is { } distinct)
                {
                    var function = _context.Operation!.FunctionBindings[(GqlNamedFunctionCallExpression)expression];
                    bool skip = false;
                    if (function.NullBehavior == GraphNullBehavior.ReturnsNullOnNullInput)
                    { for (int j = 0; j < values.Length; j++) { skip |= values[j] is null; } }
                    if (skip) { continue; }
                    object?[] keyValues = values.ToArray();
                    if (!distinct.Add(keyValues)) { continue; }
                    _memory?.Add(64); _memory?.RetainValue(keyValues, _token);
                }
                var arguments = new GraphArguments(values, new GraphFunctionContext(_context));
                try { group.Accumulators[i].Add(in arguments); }
                catch (GraphPlanningException error) { throw error.At(expression.Location); }
            }
        }

        internal List<Dictionary<string, object?>> Finish()
        {
            var result = new List<Dictionary<string, object?>>(_ordered.Count);
            var shape = new GraphExpressionShape(_token);
            foreach (var group in _ordered)
            {
                _token.ThrowIfCancellationRequested();
                var values = new Dictionary<GqlExpression, object?>(shape);
                for (int i = 0; i < _plan.Keys.Count; i++) { _token.ThrowIfCancellationRequested(); values.Add(_plan.Keys[i], group.Key[i]); }
                for (int i = 0; i < _plan.Aggregates.Count; i++)
                { _token.ThrowIfCancellationRequested(); try { values.Add(_plan.Aggregates[i], group.Accumulators[i].Finish()); } catch (GraphPlanningException error) { throw error.At(_plan.Aggregates[i].Location); } }
                _memory?.Add(128 + 80L * values.Count);
                foreach (var value in values.Values) { _memory?.RetainValue(value, _token); }
                result.Add(new GraphGroupedBinding(values));
            }
            return result;
        }

        private Group AddGroup(object?[] key)
        {
            _memory?.Add(256 + 512L * _plan.Aggregates.Count);
            _memory?.RetainValue(key, _token);
            var accumulators = new GraphAggregateAccumulator[_plan.Aggregates.Count];
            var distinct = new HashSet<object?[]>?[accumulators.Length];
            try
            {
                var context = new GraphFunctionContext(_context);
                for (int i = 0; i < accumulators.Length; i++)
                {
                    GqlExpression expression = _plan.Aggregates[i];
                    GraphAggregateFunction function = expression is GqlAggregateExpression native
                        ? (_context.Database?.Engine.Functions ?? GraphFunctionCatalog.Standard).Native(native.Function)
                        : (GraphAggregateFunction)_context.Operation!.FunctionBindings[(GqlNamedFunctionCallExpression)expression];
                    try
                    {
                        accumulators[i] = function.CreateAccumulator(in context);
                        accumulators[i].Own(_memory);
                        if (accumulators[i] is GraphNativeAggregateAccumulator builtin)
                        {
                            var nativeExpression = expression as GqlAggregateExpression ??
                                new GqlAggregateExpression(((GraphNativeAggregateFunction)function).Identity,
                                    ((GqlNamedFunctionCallExpression)expression).Arguments.FirstOrDefault(),
                                    ((GqlNamedFunctionCallExpression)expression).Distinct, expression.Location);
                            builtin.Configure(nativeExpression, _memory);
                        }
                        else if (((GqlNamedFunctionCallExpression)expression).Distinct) { distinct[i] = new HashSet<object?[]>(GraphRowComparer.Instance); }
                    }
                    catch (GraphPlanningException error) { throw error.At(expression.Location); }
                }
            }
            catch { foreach (var accumulator in accumulators) { try { accumulator?.Dispose(); } catch { /* Preserve the creation/budget failure while releasing every group. */ } } throw; }
            var group = new Group(key, accumulators, distinct);
            _groups.Add(key, group);
            _ordered.Add(group);
            return group;
        }

        internal void DisposeAfterFailure()
        {
            try { Dispose(); }
            catch { /* Cleanup must not replace the statement's original failure identity. */ }
        }

        public void Dispose()
        {
            Exception? failure = null;
            foreach (var group in _ordered)
            {
                foreach (var accumulator in group.Accumulators)
                {
                    try { accumulator.Dispose(); }
                    catch (Exception error) { failure ??= error; }
                }
            }
            _groups.Clear();
            _ordered.Clear();
            if (failure is not null) { System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(failure).Throw(); }
        }

        private sealed class DebugView
        {
            public DebugView(global::Assimalign.Cohesion.Database.Graph.Internal.GraphGrouping.Accumulator value)
            {
                _ = value;
            }
        }
    }

    private sealed record Group(object?[] Key, GraphAggregateAccumulator[] Accumulators, HashSet<object?[]>?[] Distinct);
}

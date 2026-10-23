using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using Assimalign.Cohesion.Database.Graph.Language;

namespace Assimalign.Cohesion.Database.Graph.Internal;

internal static class GraphGrouping
{
    internal static GraphGroupingPlan? Plan(GqlQueryExpression query, IReadOnlyList<GraphProjectionPlan> projections, CancellationToken token,
        Action<GqlExpression>? validateKey = null)
    {
        var shape = new GraphExpressionShape(token);
        var aggregates = new HashSet<GqlExpression>(shape);
        var keys = new HashSet<GqlExpression>(shape);
        var aggregateList = new List<GqlAggregateExpression>();
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
                if (item.Expression is GqlAggregateExpression aggregate)
                {
                    if (inside) { throw new GraphPlanningException("CG05001", ErrorMessages.AggregatePlacement, aggregate.Location); }
                    aggregated = inside = true;
                    if (aggregates.Add(aggregate)) { aggregateList.Add(aggregate); }
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
        var scope = new GraphGroupingScope(keyList, aggregateList, shape, token);
        var plan = new GraphGroupingPlan(keyList.AsReadOnly(), aggregateList.AsReadOnly(), scope);
        foreach (var projection in projections) { scope.Validate(projection.Expression); }
        return plan;
    }

    /// <summary>Consumes one borrowed binding at a time; groups retain keys and aggregate state only.</summary>
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
                var aggregate = _plan.Aggregates[i];
                group.Accumulators[i].Add(aggregate.Operand is null ? 1L
                    : GraphExpressionEvaluator.Evaluate(aggregate.Operand, binding, _context, _parameters), _token);
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
                { _token.ThrowIfCancellationRequested(); values.Add(_plan.Aggregates[i], group.Accumulators[i].Finish()); }
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
            var group = new Group(key, _plan.Aggregates.Select(aggregate => new GraphAggregateAccumulator(aggregate, _memory)).ToArray());
            _groups.Add(key, group);
            _ordered.Add(group);
            return group;
        }

        public void Dispose()
        {
            _groups.Clear();
            _ordered.Clear();
        }
    }

    private sealed record Group(object?[] Key, GraphAggregateAccumulator[] Accumulators);
}

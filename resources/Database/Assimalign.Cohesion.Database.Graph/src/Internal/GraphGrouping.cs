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
                    if (inside) { throw new GraphPlanningException("COHDBG001", ErrorMessages.AggregatePlacement, aggregate.Location); }
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
        if (projections.Count == 0) { throw new GraphPlanningException("COHDBG001", ErrorMessages.GroupByMismatch, query.Location); }
        if (query.GroupBy is not null)
        {
            var explicitKeys = new List<GqlExpression>();
            foreach (var key in query.GroupBy)
            {
                token.ThrowIfCancellationRequested();
                if (key is null) { throw new GraphPlanningException("COHDBG001", ErrorMessages.GroupByMismatch, query.Location); }
                GqlExpression resolved = key;
                // Neo4j AggregationChecker.scala:117-144 exempts non-aggregate grouping
                // aliases and diagnoses an aggregate alias. Expressions retain input scope.
                if (key is GqlVariableExpression name && groupingNames.TryGetValue(name.Variable, out var item))
                { resolved = item ?? throw new GraphPlanningException("COHDBG001", ErrorMessages.AggregatePlacement, key.Location); }
                validateKey?.Invoke(resolved);
                explicitKeys.Add(resolved);
            }
            if (explicitKeys.Count != keys.Count || !keys.SetEquals(explicitKeys))
            { throw new GraphPlanningException("COHDBG001", ErrorMessages.GroupByMismatch, query.Location); }
        }
        var scope = new GraphGroupingScope(keyList, aggregateList, shape, token);
        var plan = new GraphGroupingPlan(keyList.AsReadOnly(), aggregateList.AsReadOnly(), scope);
        foreach (var projection in projections) { scope.Validate(projection.Expression); }
        return plan;
    }

    internal static List<Dictionary<string, object?>> Apply(GraphGroupingPlan plan,
        IReadOnlyList<Dictionary<string, object?>> bindings, IReadOnlyDictionary<string, object?>? parameters, CancellationToken token)
    {
        var groups = new Dictionary<object?[], Group>(GraphRowComparer.Instance);
        var ordered = new List<Group>();
        // An ungrouped aggregate has exactly one group even when MATCH produced no rows.
        if (plan.Keys.Count == 0) { AddGroup([]); }
        foreach (var binding in bindings)
        {
            token.ThrowIfCancellationRequested();
            var key = new object?[plan.Keys.Count];
            for (int i = 0; i < key.Length; i++)
            { token.ThrowIfCancellationRequested(); key[i] = GraphExpressionEvaluator.Evaluate(plan.Keys[i], binding, parameters); }
            if (!groups.TryGetValue(key, out var group)) { group = AddGroup(key); }
            for (int i = 0; i < group.Accumulators.Length; i++)
            {
                token.ThrowIfCancellationRequested();
                var aggregate = plan.Aggregates[i];
                group.Accumulators[i].Add(aggregate.Operand is null ? 1L
                    : GraphExpressionEvaluator.Evaluate(aggregate.Operand, binding, parameters), token);
            }
        }
        var result = new List<Dictionary<string, object?>>(ordered.Count);
        var shape = new GraphExpressionShape(token);
        foreach (var group in ordered)
        {
            token.ThrowIfCancellationRequested();
            var values = new Dictionary<GqlExpression, object?>(shape);
            for (int i = 0; i < plan.Keys.Count; i++) { token.ThrowIfCancellationRequested(); values.Add(plan.Keys[i], group.Key[i]); }
            for (int i = 0; i < plan.Aggregates.Count; i++)
            { token.ThrowIfCancellationRequested(); values.Add(plan.Aggregates[i], group.Accumulators[i].Finish()); }
            result.Add(new GraphGroupedBinding(values));
        }
        return result;

        Group AddGroup(object?[] key)
        {
            var group = new Group(key, plan.Aggregates.Select(aggregate => new GraphAggregateAccumulator(aggregate)).ToArray());
            groups.Add(key, group);
            ordered.Add(group);
            return group;
        }
    }

    private sealed record Group(object?[] Key, GraphAggregateAccumulator[] Accumulators);
}

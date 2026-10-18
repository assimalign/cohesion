using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using Assimalign.Cohesion.Database.Graph.Language;

namespace Assimalign.Cohesion.Database.Graph.Internal;

internal static class GraphGrouping
{
    internal static GraphGroupingPlan? Plan(GqlQueryExpression query, IReadOnlyList<GraphProjectionPlan> projections, CancellationToken token)
    {
        var shape = new GraphExpressionShape(token);
        var aggregates = new HashSet<GqlExpression>(shape);
        var keys = new HashSet<GqlExpression>(shape);
        var aggregateList = new List<GqlAggregateExpression>();
        var keyList = new List<GqlExpression>();
        var candidates = new List<GqlExpression>();
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
        }
        if (query.GroupBy is null && aggregateList.Count == 0) { return null; }
        foreach (var candidate in candidates)
        { token.ThrowIfCancellationRequested(); if (keys.Add(candidate)) { keyList.Add(candidate); } }
        if (projections.Count == 0) { throw new GraphPlanningException("COHDBG001", ErrorMessages.GroupByMismatch, query.Location); }
        if (query.GroupBy is not null && (query.GroupBy.Count != keys.Count || !keys.SetEquals(query.GroupBy)))
        { throw new GraphPlanningException("COHDBG001", ErrorMessages.GroupByMismatch, query.Location); }
        var plan = new GraphGroupingPlan(keyList.AsReadOnly(), aggregateList.AsReadOnly());
        foreach (var projection in projections) { ValidateScope(projection.Expression, plan, null, token); }
        return plan;
    }

    internal static void ValidateScope(GqlExpression root, GraphGroupingPlan plan,
        IReadOnlyDictionary<string, int>? aliases, CancellationToken token)
    {
        var shape = new GraphExpressionShape(token);
        var keys = new HashSet<GqlExpression>(plan.Keys, shape);
        var aggregates = new HashSet<GqlExpression>(plan.Aggregates, shape);
        var variables = plan.Keys.OfType<GqlVariableExpression>().Select(key => key.Variable).ToHashSet(StringComparer.Ordinal);
        var pending = new Stack<GqlExpression>();
        pending.Push(root);
        while (pending.TryPop(out var expression))
        {
            token.ThrowIfCancellationRequested();
            if (keys.Contains(expression) || aggregates.Contains(expression)) { continue; }
            string? name = expression switch
            {
                GqlVariableExpression variable => variable.Variable,
                GqlPropertyExpression property => property.Variable,
                GqlLabeledPredicate labeled => labeled.Variable,
                _ => null,
            };
            if (name is not null && (variables.Contains(name) || aliases is not null && aliases.ContainsKey(name))) { continue; }
            if (name is not null || expression is GqlAggregateExpression)
            { throw new GraphPlanningException("COHDBG001", ErrorMessages.GroupedExpressionRequired, expression.Location); }
            foreach (var child in GraphValueExpressions.Children(expression)) { pending.Push(child); }
        }
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

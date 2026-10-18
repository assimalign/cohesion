using System;
using System.Collections.Generic;
using System.Threading;
using Assimalign.Cohesion.Database.Graph.Language;

namespace Assimalign.Cohesion.Database.Graph.Internal;

/// <summary>One materialized projection and its pre-evaluated ordering keys.</summary>
internal sealed record GraphOrderedRow(object?[] Values, object?[] Keys, int Ordinal);

internal static class GraphSortPage
{
    internal static object?[] Keys(GraphPlan plan, Dictionary<string, object?> binding, object?[] row)
    {
        if (plan.SortPage is not { Keys.Count: > 0 } stage) { return []; }
        var scope = new Dictionary<string, object?>(binding, StringComparer.Ordinal);
        for (int i = 0; i < plan.Projections.Count; i++) { scope[plan.Projections[i].Name] = row[i]; }
        var keys = new object?[stage.Keys.Count];
        for (int i = 0; i < keys.Length; i++)
        {
            var key = stage.Keys[i];
            keys[i] = key.ProjectionIndex >= 0 ? row[key.ProjectionIndex]
                : GraphExpressionEvaluator.Evaluate(key.Item.Expression, scope, plan.Parameters);
        }
        return keys;
    }

    internal static object?[][] Apply(List<GraphOrderedRow> rows, GraphSortPagePlan? stage, CancellationToken token)
    {
        if (stage?.Keys.Count > 0)
        {
            // Sort only cached values, with an explicit ordinal tie-break for deterministic ties.
            rows.Sort((left, right) => Compare(left, right, stage.Keys));
        }
        token.ThrowIfCancellationRequested();
        int start = (int)Math.Min(stage?.Offset ?? 0, rows.Count);
        int count = (int)Math.Min(stage?.Limit ?? rows.Count, rows.Count - start);
        var result = new object?[count][];
        for (int i = 0; i < count; i++) { result[i] = rows[start + i].Values; }
        return result;
    }

    private static int Compare(GraphOrderedRow left, GraphOrderedRow right, IReadOnlyList<GraphSortKeyPlan> keys)
    {
        for (int i = 0; i < keys.Count; i++)
        {
            object? a = left.Keys[i], b = right.Keys[i];
            var item = keys[i].Item;
            int order;
            if (a is null || b is null)
            {
                bool first = item.NullOrdering == GqlNullOrdering.First ||
                    item.NullOrdering == GqlNullOrdering.Default && item.Direction == GqlSortDirection.Descending;
                order = a is null ? b is null ? 0 : first ? -1 : 1 : first ? 1 : -1;
            }
            else
            {
                order = GraphOrderComparer.Compare(a, b);
                if (item.Direction == GqlSortDirection.Descending) { order = -Math.Sign(order); }
            }
            if (order != 0) { return order; }
        }
        return left.Ordinal.CompareTo(right.Ordinal);
    }
}

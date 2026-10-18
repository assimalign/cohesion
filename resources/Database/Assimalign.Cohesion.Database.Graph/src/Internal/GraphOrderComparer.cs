using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.CompilerServices;

namespace Assimalign.Cohesion.Database.Graph.Internal;

/// <summary>Total Neo4j orderability for the supported graph value families.</summary>
internal static class GraphOrderComparer
{
    internal static int Compare(object? left, object? right)
    {
        RuntimeHelpers.EnsureSufficientExecutionStack();
        if (ReferenceEquals(left, right)) { return 0; }
        int group = Group(left).CompareTo(Group(right));
        if (group != 0) { return group; }
        return (left, right) switch
        {
            (IReadOnlyDictionary<string, object?> a, IReadOnlyDictionary<string, object?> b) => Maps(a, b),
            (object?[] a, object?[] b) => Lists(a, b),
            (GraphNode a, GraphNode b) => a.Id.Value.CompareTo(b.Id.Value),
            (GraphRelationship a, GraphRelationship b) => a.Id.Value.CompareTo(b.Id.Value),
            (GraphPath a, GraphPath b) => Paths(a, b),
            (string a, string b) => StringComparer.Ordinal.Compare(a, b),
            (bool a, bool b) => a.CompareTo(b),
            _ => GraphValueComparer.CompareNumbers(left!, right!),
        };
    }

    // AnyValueComparator.java:44-85 and VirtualValueGroup.java:26-32, local Neo4j values tree.
    private static int Group(object? value) => value switch
    {
        IReadOnlyDictionary<string, object?> => 0,
        GraphNode => 1,
        GraphRelationship => 2,
        object?[] => 3,
        GraphPath => 4,
        string => 5,
        bool => 6,
        null => 8,
        _ => 7,
    };

    private static int Lists(object?[] left, object?[] right)
    {
        for (int i = 0; i < Math.Min(left.Length, right.Length); i++)
        { int order = Compare(left[i], right[i]); if (order != 0) { return order; } }
        return left.Length.CompareTo(right.Length);
    }

    // MapValue.java:538-564: size, sorted keys, then corresponding values.
    private static int Maps(IReadOnlyDictionary<string, object?> left, IReadOnlyDictionary<string, object?> right)
    {
        int order = left.Count.CompareTo(right.Count);
        if (order != 0) { return order; }
        var keys = left.Keys.Order(StringComparer.Ordinal).ToArray();
        var other = right.Keys.Order(StringComparer.Ordinal).ToArray();
        for (int i = 0; i < keys.Length; i++)
        { order = StringComparer.Ordinal.Compare(keys[i], other[i]); if (order != 0) { return order; } }
        foreach (string key in keys)
        { order = Compare(left[key], right[key]); if (order != 0) { return order; } }
        return 0;
    }

    // PathValue.java:171-189: starting node, relationship sequence, then length.
    private static int Paths(GraphPath left, GraphPath right)
    {
        int order = Compare(left.Nodes[0], right.Nodes[0]);
        if (order != 0) { return order; }
        for (int i = 0; i < Math.Min(left.Relationships.Count, right.Relationships.Count); i++)
        { order = Compare(left.Relationships[i], right.Relationships[i]); if (order != 0) { return order; } }
        return left.Relationships.Count.CompareTo(right.Relationships.Count);
    }
}

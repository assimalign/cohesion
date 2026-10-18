using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using System.Threading;

namespace Assimalign.Cohesion.Database.Graph.Internal;

/// <summary>Total Neo4j orderability for the supported graph value families.</summary>
internal static class GraphOrderComparer
{
    private sealed record PreparedMap(string[] Keys, object?[] Values);

    // Canonicalize every map once, including maps nested inside maps and lists. These private
    // values belong only to the ordering keys; projected values retain their original shape.
    internal static object? Prepare(object? value, CancellationToken token = default)
    {
        token.ThrowIfCancellationRequested();
        RuntimeHelpers.EnsureSufficientExecutionStack();
        if (value is IReadOnlyDictionary<string, object?> map)
        {
            var keys = new string[map.Count];
            int index = 0;
            foreach (string key in map.Keys) { token.ThrowIfCancellationRequested(); keys[index++] = key; }
            Array.Sort(keys, StringComparer.Ordinal);
            var values = new object?[keys.Length];
            for (int i = 0; i < keys.Length; i++) { values[i] = Prepare(map[keys[i]], token); }
            return new PreparedMap(keys, values);
        }
        if (value is object?[] list)
        {
            var values = new object?[list.Length];
            for (int i = 0; i < list.Length; i++) { values[i] = Prepare(list[i], token); }
            return values;
        }
        return value;
    }

    internal static int Compare(object? left, object? right, CancellationToken token = default)
    {
        token.ThrowIfCancellationRequested();
        RuntimeHelpers.EnsureSufficientExecutionStack();
        if (ReferenceEquals(left, right)) { return 0; }
        // Integer literal fields dominate map keys. Avoid family dispatch and exact-fraction
        // construction for identical numeric tags; mixed tags retain exact value ordering.
        if (left is long integer && right is long other) { return integer.CompareTo(other); }
        int group = Group(left).CompareTo(Group(right));
        if (group != 0) { return group; }
        return (left, right) switch
        {
            (PreparedMap a, PreparedMap b) => Maps(a, b, token),
            (object?[] a, object?[] b) => Lists(a, b, token),
            (GraphNode a, GraphNode b) => a.Id.Value.CompareTo(b.Id.Value),
            (GraphRelationship a, GraphRelationship b) => a.Id.Value.CompareTo(b.Id.Value),
            (GraphPath a, GraphPath b) => Paths(a, b, token),
            (string a, string b) => StringComparer.Ordinal.Compare(a, b),
            (bool a, bool b) => a.CompareTo(b),
            (int a, int b) => a.CompareTo(b),
            (short a, short b) => a.CompareTo(b),
            (sbyte a, sbyte b) => a.CompareTo(b),
            (ulong a, ulong b) => a.CompareTo(b),
            (uint a, uint b) => a.CompareTo(b),
            (ushort a, ushort b) => a.CompareTo(b),
            (byte a, byte b) => a.CompareTo(b),
            (decimal a, decimal b) => a.CompareTo(b),
            (double a, double b) => a.CompareTo(b),
            (float a, float b) => a.CompareTo(b),
            _ => GraphOrderNumbers.Compare(left!, right!),
        };
    }

    // AnyValueComparator.java:44-85 and VirtualValueGroup.java:26-32, local Neo4j values tree.
    private static int Group(object? value) => value switch
    {
        PreparedMap => 0,
        GraphNode => 1,
        GraphRelationship => 2,
        object?[] => 3,
        GraphPath => 4,
        string => 5,
        bool => 6,
        null => 8,
        _ => 7,
    };

    private static int Lists(object?[] left, object?[] right, CancellationToken token)
    {
        for (int i = 0; i < Math.Min(left.Length, right.Length); i++)
        { int order = Compare(left[i], right[i], token); if (order != 0) { return order; } }
        return left.Length.CompareTo(right.Length);
    }

    // MapValue.java:538-564: size, sorted keys, then corresponding values.
    private static int Maps(PreparedMap left, PreparedMap right, CancellationToken token)
    {
        int order = left.Keys.Length.CompareTo(right.Keys.Length);
        if (order != 0) { return order; }
        for (int i = 0; i < left.Keys.Length; i++)
        { token.ThrowIfCancellationRequested(); order = StringComparer.Ordinal.Compare(left.Keys[i], right.Keys[i]); if (order != 0) { return order; } }
        for (int i = 0; i < left.Values.Length; i++)
        { order = Compare(left.Values[i], right.Values[i], token); if (order != 0) { return order; } }
        return 0;
    }

    // PathValue.java:171-189: starting node, relationship sequence, then length.
    private static int Paths(GraphPath left, GraphPath right, CancellationToken token)
    {
        int order = left.Nodes[0].Id.Value.CompareTo(right.Nodes[0].Id.Value);
        if (order != 0) { return order; }
        for (int i = 0; i < Math.Min(left.Relationships.Count, right.Relationships.Count); i++)
        { token.ThrowIfCancellationRequested(); order = left.Relationships[i].Id.Value.CompareTo(right.Relationships[i].Id.Value); if (order != 0) { return order; } }
        return left.Relationships.Count.CompareTo(right.Relationships.Count);
    }
}

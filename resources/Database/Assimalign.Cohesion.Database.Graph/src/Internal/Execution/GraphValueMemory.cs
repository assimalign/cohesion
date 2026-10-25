using System;
using System.Collections;
using System.Collections.Generic;
using System.Threading;

using Assimalign.Cohesion.Database.Graph.Protocol;

namespace Assimalign.Cohesion.Database.Graph.Internal;

/// <summary>Conservative retained-value accounting, independent of GC timing and shared process heaps.</summary>
internal static class GraphValueMemory
{
    internal static long Node(GraphNode node, CancellationToken token)
    {
        // Stored graph properties are scalar. The DFS cursor owns a decoded target until
        // that child's complete subtree has finished; no identity-table traversal is needed.
        long bytes = 224 + 16L * node.Labels.Count + 80L * node.Properties.Count;
        for (int i = 0; i < node.Labels.Count; i++) { token.ThrowIfCancellationRequested(); bytes += Shallow(node.Labels[i]); }
        foreach (var property in node.Properties)
        { token.ThrowIfCancellationRequested(); bytes += Shallow(property.Key) + (property.Value is null ? 0 : Shallow(property.Value)); }
        return bytes;
    }

    internal static bool IsTracked(object value)
        => value switch
        {
            // Small reference leaves and boxed scalars can be shared by retained rows too.
            // Count their storage once, just like containers, rather than per reference.
            string or byte[] => true,
            GraphNode or GraphRelationship => false, // Entity structs retain inline data per occurrence.
            _ => value is ValueType or IList or IReadOnlyList<GraphNode> or IReadOnlyList<GraphRelationship> or IReadOnlyDictionary<string, object?> or GraphPath or GraphOrderComparer.PreparedMap,
        };

    internal static long Shallow(object value) => value switch
    {
        string text => 32L + 2L * text.Length,
        byte[] bytes => 32L + bytes.Length,
        GraphNode => 64,
        GraphRelationship => 80,
        GraphPath => 64,
        IReadOnlyList<GraphNode> nodes => 32L + 64L * nodes.Count,
        IReadOnlyList<GraphRelationship> edges => 32L + 80L * edges.Count,
        IReadOnlyDictionary<string, object?> map => 128L + 80L * map.Count,
        GraphOrderComparer.PreparedMap map => 64L + 16L * map.Keys.Length,
        IList list => 32L + 16L * list.Count,
        _ => 32,
    };

    internal static void PushChildren(object value, Stack<object?> pending)
    {
        switch (value)
        {
            case string or byte[]: break;
            case GraphNode node: pending.Push(node.Labels); pending.Push(node.Properties); break;
            case GraphRelationship edge: pending.Push(edge.Type); pending.Push(edge.Properties); break;
            case GraphPath path:
                pending.Push(path.Nodes); pending.Push(path.Relationships);
                break;
            case IReadOnlyList<GraphNode> nodes:
                for (int i = 0; i < nodes.Count; i++) { var node = nodes[i]; pending.Push(node.Labels); pending.Push(node.Properties); }
                break;
            case IReadOnlyList<GraphRelationship> edges:
                for (int i = 0; i < edges.Count; i++) { var edge = edges[i]; pending.Push(edge.Type); pending.Push(edge.Properties); }
                break;
            case IReadOnlyDictionary<string, object?> map:
                foreach (var pair in map) { pending.Push(pair.Key); pending.Push(pair.Value); }
                break;
            case GraphOrderComparer.PreparedMap map:
                foreach (var key in map.Keys) { pending.Push(key); }
                foreach (var element in map.Values) { pending.Push(element); }
                break;
            case IList list:
                for (int i = 0; i < list.Count; i++) { pending.Push(list[i]); }
                break;
        }
    }

}

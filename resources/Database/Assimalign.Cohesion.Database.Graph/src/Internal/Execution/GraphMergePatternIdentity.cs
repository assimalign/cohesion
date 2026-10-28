using System;
using System.Collections.Generic;
using System.Linq;

using Assimalign.Cohesion.Database.Transactions;

namespace Assimalign.Cohesion.Database.Graph.Internal;

/// <summary>
/// Logical lock target after binding and evaluating property equalities. Node positions are
/// binding slots (reuse a position for a repeated variable); variable spellings are absent.
/// This is a lock identity, not a matcher or a uniqueness constraint.
/// </summary>
[System.Diagnostics.DebuggerDisplay("GraphMergePatternIdentity")]
[System.Diagnostics.DebuggerTypeProxy(typeof(GraphMergePatternIdentity.DebugView))]
internal sealed class GraphMergePatternIdentity
{
    internal GraphMergePatternIdentity(IReadOnlyList<GraphMergeNodeIdentity> nodes,
        IReadOnlyList<GraphMergeRelationshipIdentity> relationships)
    {
        ArgumentNullException.ThrowIfNull(nodes);
        ArgumentNullException.ThrowIfNull(relationships);
        if (nodes.Count == 0) { throw InvalidIdentity(); }
        var hash = new HashCode();
        var bound = new HashSet<GraphMergeEntity>();
        var slots = new int[nodes.Count];
        hash.Add(nodes.Count);
        for (int slot = 0; slot < nodes.Count; slot++)
        {
            var node = nodes[slot];
            if (node.Labels is null || node.Properties is null) { throw InvalidIdentity(); }
            var nodeHash = new HashCode();
            nodeHash.Add(node.BoundId);
            AddBound(true, node.BoundId);
            var labels = node.Labels.Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray();
            nodeHash.Add(labels.Length);
            foreach (string label in labels)
            {
                if (string.IsNullOrWhiteSpace(label)) { throw InvalidIdentity(); }
                nodeHash.Add(label, StringComparer.Ordinal);
            }
            AddProperties(ref nodeHash, node.Properties);
            slots[slot] = nodeHash.ToHashCode();
        }
        foreach (int node in slots.Order()) { hash.Add(node); }
        // Incoming is represented by swapping endpoint slots. Undirected endpoints have no order.
        // Endpoint tokens depend on node contents, never source positions. Equal node tokens
        // may conservatively collapse distinct topologies, like any other lock-hash collision.
        // Sorting both multisets makes reordered nodes and independent parts equivalent.
        var edges = new List<int>(relationships.Count);
        foreach (var edge in relationships)
        {
            if ((uint)edge.From >= (uint)nodes.Count || (uint)edge.To >= (uint)nodes.Count ||
                string.IsNullOrWhiteSpace(edge.Type) || edge.Properties is null) { throw InvalidIdentity(); }
            int from = slots[edge.From], to = slots[edge.To];
            if (edge.Undirected && from > to) { (from, to) = (to, from); }
            var edgeHash = new HashCode();
            edgeHash.Add(from); edgeHash.Add(to); edgeHash.Add(edge.Undirected);
            edgeHash.Add(edge.Type, StringComparer.Ordinal);
            edgeHash.Add(edge.BoundId);
            AddBound(false, edge.BoundId);
            AddProperties(ref edgeHash, edge.Properties);
            edges.Add(edgeHash.ToHashCode());
        }
        hash.Add(edges.Count);
        foreach (int edge in edges.Order()) { hash.Add(edge); }
        // Graph reserves this object namespace for logical MERGE patterns. Hash collisions only
        // over-serialize; the database writer lock remains mandatory even for different hashes.
        Resource = LockResource.Entry(GraphLockNamespaces.MergePatterns, (uint)hash.ToHashCode());
        BoundEntities = Array.AsReadOnly(bound.ToArray());

        void AddBound(bool node, ulong? id)
        {
            if (id == 0) { throw InvalidIdentity(); }
            if (id is { } value) { bound.Add(new GraphMergeEntity(node, value)); }
        }
    }

    internal LockResource Resource { get; }
    internal IReadOnlyList<GraphMergeEntity> BoundEntities { get; }

    private static void AddProperties(ref HashCode hash, IReadOnlyDictionary<string, object?> properties)
    {
        hash.Add(properties.Count);
        foreach (var property in properties.OrderBy(item => item.Key, StringComparer.Ordinal))
        {
            if (string.IsNullOrWhiteSpace(property.Key) || property.Value is not (bool or string or
                byte or sbyte or short or ushort or int or uint or long or ulong or decimal or float or double) ||
                property.Value is double d && !double.IsFinite(d) || property.Value is float f && !float.IsFinite(f))
            { throw InvalidIdentity(); }
            hash.Add(property.Key, StringComparer.Ordinal);
            // Exact reduced numeric fractions: 1, 1m and 1d are one logical equality;
            // adjacent Int64 values above 2^53 remain distinct.
            hash.Add(GraphValueComparer.Instance.GetHashCode(property.Value));
        }
    }

    private static GraphPlanningException InvalidIdentity()
        => new("CG05023", ErrorMessages.MergeIdentityInvalid);

    private sealed class DebugView
    {
        public DebugView(GraphMergePatternIdentity value) { _ = value; }
    }
}

internal readonly record struct GraphMergeNodeIdentity(ulong? BoundId, IReadOnlyList<string> Labels,
    IReadOnlyDictionary<string, object?> Properties);

internal readonly record struct GraphMergeRelationshipIdentity(int From, int To, string Type, bool Undirected,
    ulong? BoundId, IReadOnlyDictionary<string, object?> Properties);

internal readonly record struct GraphMergeEntity(bool Node, ulong Id);

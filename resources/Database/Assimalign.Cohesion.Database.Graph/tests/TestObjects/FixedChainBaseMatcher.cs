using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

using Assimalign.Cohesion.Database.Graph.Internal;
using Assimalign.Cohesion.Database.Graph.Language;
using Assimalign.Cohesion.Database.Graph.Storage;

using Assimalign.Cohesion.Database.Graph.Protocol;

namespace Assimalign.Cohesion.Database.Graph.Tests.TestObjects;

// Frozen pre-quantifier matcher from e6a3da768, GraphPlanExecutor.cs:332-436,455-471.
// Keep its traversal and property evaluation order independent of the current matcher.
internal static class FixedChainBaseMatcher
{
    internal static async ValueTask<List<Dictionary<string, object?>>> MatchAsync(GraphDatabase database,
        GraphOperation operation, GraphPathPlan plan, CancellationToken token)
    {
        var output = new List<Dictionary<string, object?>>();
        await MatchCoreAsync(database, operation, plan, new(StringComparer.Ordinal),
            (bindings, _, _) => output.Add(bindings), new GraphPlanExecutor.ExpansionBudget(), null, token).ConfigureAwait(false);
        return output;
    }

    private static async ValueTask MatchCoreAsync(GraphDatabase database, GraphOperation operation, GraphPathPlan plan,
        Dictionary<string, object?> input, Action<Dictionary<string, object?>, GraphNode[], GraphRelationship[]> acceptMatch,
        GraphPlanExecutor.ExpansionBudget budget, IReadOnlyDictionary<string, object?>? parameters, CancellationToken token, bool deferUnbound = false)
    {
        token.ThrowIfCancellationRequested();
        operation.EnsureActive();
        var path = plan.Pattern;
        var anchor = plan.Anchor;
        // Explicit NULL is already bound. It cannot match an element and must never become
        // an unbound full-scan anchor, including at a non-anchor position in the same path.
        foreach (var node in path.Nodes)
        {
            if (node.Variable is not { } name || !input.TryGetValue(name, out var value)) { continue; }
            if (value is null) { return; }
            if (value is not GraphNode) { throw new GraphPlanningException("COHDBG013", ErrorMessages.EntityBindingRequired); }
        }
        foreach (var edge in path.Relationships)
        {
            if (edge.Variable is not { } name || !input.TryGetValue(name, out var value)) { continue; }
            if (value is null) { return; }
            if (value is not GraphRelationship) { throw new GraphPlanningException("COHDBG013", ErrorMessages.EntityBindingRequired); }
        }
        IReadOnlyList<StoredGraphNode> candidates;
        if (path.Nodes[anchor.NodeIndex].Variable is { } variable && input.TryGetValue(variable, out var existing) && existing is GraphNode bound)
        {
            candidates = database.Store.FindNode(bound.Id.Value, operation.Context.Snapshot) is { } node ? [node] : [];
        }
        else if (anchor.NodeIndex < path.Relationships.Count &&
            path.Relationships[anchor.NodeIndex] is { Variable: { } edgeVariable } edgePattern &&
            input.TryGetValue(edgeVariable, out var edgeBinding) && edgeBinding is GraphRelationship boundEdge)
        {
            // This anchor is the left endpoint of an already-bound relationship. Direction fixes
            // that endpoint; an undirected pattern tries both, once each even for a self-loop.
            ulong left = edgePattern.Direction == GqlPatternDirection.Incoming ? boundEdge.To.Value : boundEdge.From.Value;
            var endpoints = new List<StoredGraphNode>(2);
            if (database.Store.FindNode(left, operation.Context.Snapshot) is { } node) { endpoints.Add(node); }
            if (edgePattern.Direction is GqlPatternDirection.Undirected or GqlPatternDirection.LeftOrRight &&
                boundEdge.From != boundEdge.To && database.Store.FindNode(boundEdge.To.Value, operation.Context.Snapshot) is { } other)
            { endpoints.Add(other); }
            candidates = endpoints;
        }
        else if (anchor.Property is { } property)
        { candidates = await database.Store.SearchIndexAsync(anchor.Label!, property, anchor.Value!, operation.Context.Snapshot, token).ConfigureAwait(false); }
        else { candidates = database.Store.GetNodes(anchor.Label, operation.Context.Snapshot); }
        // Expand right of an arbitrary bound or indexed anchor, then left, preserving source positions.
        var steps = Enumerable.Range(anchor.NodeIndex, path.Relationships.Count - anchor.NodeIndex)
            .Select(index => (Edge: index, From: index, To: index + 1))
            .Concat(Enumerable.Range(0, anchor.NodeIndex).Reverse().Select(index => (Edge: index, From: index + 1, To: index))).ToArray();
        foreach (var candidate in candidates)
        {
            token.ThrowIfCancellationRequested();
            budget.Consume();
            var bindings = new Dictionary<string, object?>(input, StringComparer.Ordinal);
            var node = GraphDatabase.Materialize(candidate);
            if (!AcceptNode(path.Nodes[anchor.NodeIndex], node, bindings, parameters, deferUnbound: true)) { continue; }
            var positions = new GraphNode[path.Nodes.Count];
            positions[anchor.NodeIndex] = node;
            await Expand(0, positions, new GraphRelationship[path.Relationships.Count], bindings, []).ConfigureAwait(false);
        }
        async ValueTask Expand(int step, GraphNode[] positions, GraphRelationship[] relationships,
            Dictionary<string, object?> bindings, HashSet<ulong> used)
        {
            token.ThrowIfCancellationRequested();
            operation.EnsureActive();
            if (step == steps.Length)
            {
                if (path.Variable is { } pathVariable)
                { bindings.Add(pathVariable, new GraphPath(Array.AsReadOnly(positions), Array.AsReadOnly(relationships))); }
                for (int i = 0; i < positions.Length; i++)
                { if (!PropertiesMatch(path.Nodes[i].Properties, positions[i].Properties, bindings, parameters, deferUnbound)) { return; } }
                for (int i = 0; i < relationships.Length; i++)
                { if (!PropertiesMatch(path.Relationships[i].Properties, relationships[i].Properties, bindings, parameters, deferUnbound)) { return; } }
                acceptMatch(bindings, positions, relationships);
                return;
            }
            var current = steps[step];
            ulong from = positions[current.From].Id.Value;
            var pattern = path.Relationships[current.Edge];
            foreach (var edge in await database.Store.GetIncidentAsync(from, operation.Context.Snapshot, token).ConfigureAwait(false))
            {
                budget.Consume();
                if (used.Contains(edge.Id) || !GraphLabelEvaluator.Accepts(pattern, edge.Type)) { continue; }
                // A leftward expansion reverses the pattern direction, not the stored edge.
                // Undirected and LeftOrRight constrain neither end: storage holds only directed
                // relationships, so -[]- and <-[]-> both match an edge in either orientation.
                bool forward = current.To > current.From;
                bool outgoing = pattern.Direction == GqlPatternDirection.Outgoing && forward || pattern.Direction == GqlPatternDirection.Incoming && !forward;
                bool incoming = pattern.Direction == GqlPatternDirection.Incoming && forward || pattern.Direction == GqlPatternDirection.Outgoing && !forward;
                if (outgoing && edge.SourceId != from || incoming && edge.TargetId != from) { continue; }
                ulong next = edge.SourceId == from ? edge.TargetId : edge.SourceId;
                if (database.Store.FindNode(next, operation.Context.Snapshot) is not { } target) { continue; }
                var copy = new Dictionary<string, object?>(bindings, StringComparer.Ordinal);
                var relationship = GraphDatabase.Materialize(edge);
                if (!Accept(pattern.Variable, relationship, copy) || !PropertiesMatch(pattern.Properties, relationship.Properties, copy, parameters, deferUnbound: true)) { continue; }
                var node = GraphDatabase.Materialize(target);
                if (!AcceptNode(path.Nodes[current.To], node, copy, parameters, deferUnbound: true)) { continue; }
                var nextPositions = (GraphNode[])positions.Clone();
                nextPositions[current.To] = node;
                var nextRelationships = (GraphRelationship[])relationships.Clone();
                nextRelationships[current.Edge] = relationship;
                var nextUsed = new HashSet<ulong>(used) { edge.Id };
                await Expand(step + 1, nextPositions, nextRelationships, copy, nextUsed).ConfigureAwait(false);
            }
        }
    }

    private static bool AcceptNode(GqlNodePattern pattern, GraphNode node, Dictionary<string, object?> bindings, IReadOnlyDictionary<string, object?>? parameters, bool deferUnbound = false)
        => GraphLabelEvaluator.Accepts(pattern, node.Labels) && Accept(pattern.Variable, node, bindings) &&
            PropertiesMatch(pattern.Properties, node.Properties, bindings, parameters, deferUnbound);
    private static bool Accept(string? variable, object entity, Dictionary<string, object?> bindings)
    {
        if (variable is null) { return true; }
        if (bindings.TryGetValue(variable, out var existing))
        {
            return (existing, entity) switch
            { (GraphNode a, GraphNode b) => a.Id == b.Id, (GraphRelationship a, GraphRelationship b) => a.Id == b.Id, _ => false };
        }
        bindings.Add(variable, entity);
        return true;
    }
    private static bool PropertiesMatch(IReadOnlyDictionary<string, GqlExpression> expected, IReadOnlyDictionary<string, object?> actual, IReadOnlyDictionary<string, object?> bindings, IReadOnlyDictionary<string, object?>? parameters, bool deferUnbound = false)
        => expected.All(item => deferUnbound && GraphValueExpressions.ReferencesUnbound(item.Value, bindings) ||
            actual.TryGetValue(item.Key, out var value) && GraphExpressionEvaluator.Equal(GraphExpressionEvaluator.PropertyScalar(item.Value, bindings, default, parameters), value));

}

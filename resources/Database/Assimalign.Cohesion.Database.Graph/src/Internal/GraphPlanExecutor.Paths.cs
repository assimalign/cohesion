using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

using Assimalign.Cohesion.Database.Graph.Language;
using Assimalign.Cohesion.Database.Graph.Storage;

namespace Assimalign.Cohesion.Database.Graph.Internal;

internal static partial class GraphPlanExecutor
{
    private sealed record MatchedProperties(IReadOnlyDictionary<string, GqlExpression> Expected,
        IReadOnlyDictionary<string, object?> Actual, MatchedProperties? Previous);

    // Continuations and the DFS frontier live on the heap. A long trail never grows the CLR stack.
    private abstract record PathTask;
    private sealed record NodeTask(IReadOnlyList<GqlNodePattern> Patterns, PathTask? Next) : PathTask;
    private sealed record EdgeTask(GqlRelationshipPattern Pattern, bool Forward, PathTask? Next) : PathTask;
    private sealed record IncidentTask(EdgeTask Hop, IReadOnlyList<StoredGraphRelationship> Edges, int Index) : PathTask;
    private sealed record RepeatTask(GraphPathSegment Segment, bool Forward, int Count, string[] Names, PathTask? Next) : PathTask;
    private sealed record EndIterationTask(RepeatTask Repeat, PathTask? Next) : PathTask;
    private sealed record StartLeftTask(GraphNode Anchor, PathTask? Next) : PathTask;

    private sealed record PathState(GraphNode Current, Dictionary<string, object?> Bindings,
        List<GraphNode> Nodes, List<GraphRelationship> Relationships, HashSet<ulong> Used,
        Dictionary<string, object?[]> Groups, MatchedProperties? Properties, PathTask? Task)
    {
        internal PathState Fork(Dictionary<string, object?>? bindings = null) => this with
        {
            Bindings = bindings ?? new(Bindings, StringComparer.Ordinal), Nodes = new(Nodes), Relationships = new(Relationships),
            Used = new(Used), Groups = new(Groups, StringComparer.Ordinal),
        };
    }

    private static async ValueTask MatchCoreAsync(GraphDatabase database, GraphOperation operation, GraphPathPlan plan,
        Dictionary<string, object?> input, Action<Dictionary<string, object?>, MatchedProperties?> acceptMatch,
        ExpansionBudget budget, IReadOnlyDictionary<string, object?>? parameters, CancellationToken token, bool deferUnbound = false)
    {
        token.ThrowIfCancellationRequested();
        operation.EnsureActive();
        var shape = plan.Shape ?? GraphPathShape.Create(plan.Pattern);
        var anchor = plan.Anchor;
        // Only singleton bindings may anchor or constrain another occurrence of an entity.
        foreach (var node in shape.Nodes.SelectMany(nodes => nodes))
        {
            if (node.Variable is not { } name || !input.TryGetValue(name, out var value)) { continue; }
            if (value is null) { return; }
            if (value is not GraphNode) { throw new GraphPlanningException("COHDBG013", ErrorMessages.EntityBindingRequired); }
        }
        foreach (var segment in shape.Segments.Where(segment => segment.Quantifier is null))
        {
            if (segment.Body.Edge?.Variable is not { } name || !input.TryGetValue(name, out var value)) { continue; }
            if (value is null) { return; }
            if (value is not GraphRelationship) { throw new GraphPlanningException("COHDBG013", ErrorMessages.EntityBindingRequired); }
        }
        IReadOnlyList<StoredGraphNode> candidates;
        var anchorNode = shape.Nodes[anchor.NodeIndex].FirstOrDefault(node => node.Variable is { } name && input.ContainsKey(name));
        if (anchorNode?.Variable is { } variable && input[variable] is GraphNode bound)
        { candidates = operation.Updates.FindNode(bound.Id.Value) is { } node ? [node] : []; }
        else if (anchor.NodeIndex < shape.Segments.Count && shape.Segments[anchor.NodeIndex] is
            { Quantifier: null, Body.Edge: { Variable: { } edgeVariable } edgePattern } &&
            input.TryGetValue(edgeVariable, out var edgeBinding) && edgeBinding is GraphRelationship boundEdge)
        {
            ulong left = edgePattern.Direction == GqlPatternDirection.Incoming ? boundEdge.To.Value : boundEdge.From.Value;
            var endpoints = new List<StoredGraphNode>(2);
            if (operation.Updates.FindNode(left) is { } node) { endpoints.Add(node); }
            if (edgePattern.Direction is GqlPatternDirection.Undirected or GqlPatternDirection.LeftOrRight &&
                boundEdge.From != boundEdge.To && operation.Updates.FindNode(boundEdge.To.Value) is { } other)
            { endpoints.Add(other); }
            candidates = endpoints;
        }
        else if (anchor.Property is { } property)
        { candidates = await operation.Updates.SearchIndexAsync(anchor.Label!, property, anchor.Value!, token).ConfigureAwait(false); }
        else { candidates = operation.Updates.GetNodes(anchor.Label); }

        foreach (var candidate in candidates)
        {
            token.ThrowIfCancellationRequested();
            budget.Consume();
            var node = GraphDatabase.Materialize(candidate);
            PathTask? left = null;
            // Prepend continuations in reverse execution order: right, then left from the anchor.
            for (int i = 0; i < anchor.NodeIndex; i++)
            { left = SegmentTasks(shape.Segments[i], false, new NodeTask(shape.Nodes[i], left)); }
            PathTask? tasks = new StartLeftTask(node, left);
            for (int i = shape.Segments.Count - 1; i >= anchor.NodeIndex; i--)
            { tasks = SegmentTasks(shape.Segments[i], true, new NodeTask(shape.Nodes[i + 1], tasks)); }
            var frontier = new Stack<PathState>();
            frontier.Push(new PathState(node, new(input, StringComparer.Ordinal), [node], [], [], new(StringComparer.Ordinal), null,
                new NodeTask(shape.Nodes[anchor.NodeIndex], tasks)));
            while (frontier.TryPop(out var state))
            {
                token.ThrowIfCancellationRequested();
                operation.EnsureActive();
                switch (state.Task)
                {
                    case null:
                        if (plan.Pattern.Variable is { } pathVariable)
                        { state.Bindings.Add(pathVariable, new GraphPath(state.Nodes.AsReadOnly(), state.Relationships.AsReadOnly())); }
                        // Fixed chains retain the base matcher's source-ordered nodes, then edges,
                        // independently of the anchor. Repetitions retain reverse encounter order.
                        var finalProperties = shape.HasQuantifier ? state.Properties : FixedProperties(shape, state);
                        bool matches = true;
                        for (var check = finalProperties; check is not null; check = check.Previous)
                        { if (!PropertiesMatch(check.Expected, check.Actual, state.Bindings, operation.EvaluationContext, parameters, deferUnbound)) { matches = false; break; } }
                        if (matches) { acceptMatch(state.Bindings, finalProperties); }
                        break;
                    case StartLeftTask start:
                        frontier.Push(state with { Current = start.Anchor, Task = start.Next });
                        break;
                    case NodeTask nodes:
                        bool accepted = true;
                        var properties = state.Properties;
                        foreach (var pattern in nodes.Patterns)
                        {
                            if (!AcceptNode(pattern, state.Current, state.Bindings, operation.EvaluationContext, parameters, deferUnbound: true)) { accepted = false; break; }
                            if (shape.HasQuantifier) { properties = new MatchedProperties(pattern.Properties, state.Current.Properties, properties); }
                        }
                        if (accepted) { frontier.Push(state with { Properties = properties, Task = nodes.Next }); }
                        break;
                    case RepeatTask repeat:
                        if (repeat.Count == 0)
                        {
                            foreach (string name in repeat.Names)
                            { state.Groups[name] = []; state.Bindings[name] = Array.Empty<object?>(); }
                        }
                        if (repeat.Segment.Quantifier?.Max is not { } max || repeat.Count < max)
                        {
                            var iteration = state.Fork();
                            foreach (string name in repeat.Names) { iteration.Bindings.Remove(name); }
                            var end = new EndIterationTask(repeat, repeat.Next);
                            frontier.Push(iteration with { Task = BodyTasks(repeat.Segment.Body, repeat.Forward, end) });
                        }
                        if (repeat.Count >= (repeat.Segment.Quantifier?.Min ?? 1))
                        { frontier.Push(state with { Task = repeat.Next }); }
                        break;
                    case EndIterationTask end:
                        foreach (string name in end.Repeat.Names)
                        {
                            var previous = state.Groups[name];
                            var values = new object?[previous.Length + 1];
                            Array.Copy(previous, 0, values, end.Repeat.Forward ? 0 : 1, previous.Length);
                            values[end.Repeat.Forward ? previous.Length : 0] = state.Bindings[name];
                            state.Groups[name] = values;
                            state.Bindings[name] = values;
                        }
                        frontier.Push(state with { Task = end.Repeat with { Count = end.Repeat.Count + 1 } });
                        break;
                    case EdgeTask edgeTask:
                        var incident = await operation.Updates.GetIncidentAsync(state.Current.Id.Value,
                            GraphLabelEvaluator.IncidentDirection(edgeTask.Pattern, edgeTask.Forward),
                            operation.Updates.SelectTypes(edgeTask.Pattern), budget, token).ConfigureAwait(false);
                        frontier.Push(state with { Task = new IncidentTask(edgeTask, incident, 0) });
                        break;
                    case IncidentTask cursor:
                        // Resume the parent cursor after exactly one child. Sibling fan-out never
                        // retains copies of bindings, trails, identity sets or groups in the frontier.
                        if (cursor.Index == cursor.Edges.Count) { break; }
                        frontier.Push(state with { Task = cursor with { Index = cursor.Index + 1 } });
                        budget.VisitCandidate();
                        var edge = cursor.Edges[cursor.Index];
                        var hop = cursor.Hop;
                        ulong from = state.Current.Id.Value;
                        bool outgoing = hop.Pattern.Direction == GqlPatternDirection.Outgoing && hop.Forward || hop.Pattern.Direction == GqlPatternDirection.Incoming && !hop.Forward;
                        bool incoming = hop.Pattern.Direction == GqlPatternDirection.Incoming && hop.Forward || hop.Pattern.Direction == GqlPatternDirection.Outgoing && !hop.Forward;
                        if (state.Used.Contains(edge.Id) || !GraphLabelEvaluator.Accepts(hop.Pattern, edge.Type) ||
                            outgoing && edge.SourceId != from || incoming && edge.TargetId != from) { break; }
                        var bindings = new Dictionary<string, object?>(state.Bindings, StringComparer.Ordinal);
                        var relationship = GraphDatabase.Materialize(edge);
                        if (!Accept(hop.Pattern.Variable, relationship, bindings) ||
                            !PropertiesMatch(hop.Pattern.Properties, relationship.Properties, bindings, operation.EvaluationContext, parameters, deferUnbound: true)) { break; }
                        // The hop's own checks precede the cap; deferred references still qualify.
                        // A qualifying 65th edge fails before any target constraints can hide it.
                        if (state.Relationships.Count == 64) { throw new GraphPlanningException("COHDBG004", ErrorMessages.PathHopLimitExceeded); }
                        ulong next = edge.SourceId == from ? edge.TargetId : edge.SourceId;
                        if (operation.Updates.FindNode(next) is not { } target) { break; }
                        var nextNode = GraphDatabase.Materialize(target);
                        if (hop.Next is NodeTask targetNodes && targetNodes.Patterns.Any(pattern =>
                            !AcceptNode(pattern, nextNode, bindings, operation.EvaluationContext, parameters, deferUnbound: true))) { break; }
                        budget.ConsumeViable();
                        var copy = state.Fork(bindings);
                        copy.Used.Add(edge.Id);
                        if (hop.Forward) { copy.Nodes.Add(nextNode); copy.Relationships.Add(relationship); }
                        else { copy.Nodes.Insert(0, nextNode); copy.Relationships.Insert(0, relationship); }
                        frontier.Push(copy with { Current = nextNode, Task = hop.Next,
                            Properties = shape.HasQuantifier
                                ? new MatchedProperties(hop.Pattern.Properties, relationship.Properties, copy.Properties) : copy.Properties });
                        break;
                }
            }
        }
    }

    private static MatchedProperties? FixedProperties(GraphPathShape shape, PathState state)
    {
        MatchedProperties? properties = null;
        for (int i = shape.Segments.Count - 1; i >= 0; i--)
        { properties = new MatchedProperties(shape.Segments[i].Body.Edge!.Properties, state.Relationships[i].Properties, properties); }
        for (int i = shape.Nodes.Count - 1; i >= 0; i--)
        {
            for (int j = shape.Nodes[i].Count - 1; j >= 0; j--)
            { properties = new MatchedProperties(shape.Nodes[i][j].Properties, state.Nodes[i].Properties, properties); }
        }
        return properties;
    }

    private static PathTask SegmentTasks(GraphPathSegment segment, bool forward, PathTask? next)
        => segment.Quantifier is null ? BodyTasks(segment.Body, forward, next)
            : new RepeatTask(segment, forward, 0, segment.Body.Bindings().Select(binding => binding.Name).Distinct(StringComparer.Ordinal).ToArray(), next);

    private static PathTask BodyTasks(GraphPathShape body, bool forward, PathTask? next)
    {
        if (body.Edge is { } edge) { return new EdgeTask(edge, forward, next); }
        PathTask? tasks = next;
        if (forward)
        {
            for (int i = body.Segments.Count - 1; i >= 0; i--)
            { tasks = SegmentTasks(body.Segments[i], true, new NodeTask(body.Nodes[i + 1], tasks)); }
            return new NodeTask(body.Nodes[0], tasks);
        }
        for (int i = 0; i < body.Segments.Count; i++)
        { tasks = SegmentTasks(body.Segments[i], false, new NodeTask(body.Nodes[i], tasks)); }
        return new NodeTask(body.Nodes[^1], tasks);
    }
}

using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;

using Assimalign.Cohesion.Database.Graph.Language;
using Assimalign.Cohesion.Database.Graph.Storage;

using Assimalign.Cohesion.Database.Graph.Protocol;

namespace Assimalign.Cohesion.Database.Graph.Internal;

internal static partial class GraphPlanExecutor
{
    private sealed record MatchedProperties(IReadOnlyDictionary<string, GqlExpression> Expected,
        IReadOnlyDictionary<string, object?> Actual, MatchedProperties? Previous);

    // Continuations and the DFS frontier live on the heap. A long trail never grows the CLR stack.
    private abstract record PathTask;
    private sealed record NodeTask(IReadOnlyList<GqlNodePattern> Patterns, PathTask? Next) : PathTask;
    private sealed record EdgeTask(GqlRelationshipPattern Pattern, bool Forward, PathTask? Next) : PathTask;
    private sealed record IncidentTask(EdgeTask Hop, IAsyncEnumerator<StoredGraphRelationship> Edges) : PathTask
    {
        internal GraphMemoryLease? TargetMemory { get; set; }
        internal void ReleaseTarget()
        { var memory = TargetMemory; TargetMemory = null; memory?.Dispose(); }
    }
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

    private sealed class PathFrontier : IDisposable
    {
        private readonly Stack<(PathState State, long Bytes)> _states = new();
        private readonly GraphMemoryOwner _memory;

        internal PathFrontier(GraphQueryMemory memory)
        {
            _memory = new GraphMemoryOwner(memory);
        }
        internal int Count => _states.Count;
        internal void Push(PathState state)
        {
            long bytes = Size(state);
            _memory.Add(bytes);
            _states.Push((state, bytes));
        }
        internal void ResizeActive(PathState state, ref long bytes)
        {
            long next = Size(state);
            if (next > bytes) { _memory.Add(next - bytes); }
            else { _memory.Release(bytes - next); }
            bytes = next;
        }
        private static long Size(PathState state)
        {
            long bytes = 384 + 80L * state.Bindings.Count + 48L * state.Used.Count + 128L * state.Groups.Count
                + 64L * state.Nodes.Count + 80L * state.Relationships.Count;
            foreach (var values in state.Groups.Values) { bytes += 32 + 16L * values.Length; }
            return bytes;
        }
        internal PathState Pop(out long bytes)
        {
            var item = _states.Pop();
            bytes = item.Bytes;
            return item.State;
        }
        internal void Release(long bytes)
        {
            _memory.Release(bytes);
        }
        public void Dispose() { _states.Clear(); _memory.Dispose(); }
    }

    private static async IAsyncEnumerable<(Dictionary<string, object?> Bindings, MatchedProperties? Properties)> MatchCoreStreamAsync(
        GraphDatabase database, GraphOperation operation, GraphPathPlan plan, Dictionary<string, object?> input,
        ExpansionBudget budget, IReadOnlyDictionary<string, object?>? parameters,
        [EnumeratorCancellation] CancellationToken token, bool deferUnbound = false)
    {
        token.ThrowIfCancellationRequested();
        operation.EnsureActive();
        var shape = plan.Shape ?? GraphPathShape.Create(plan.Pattern);
        var anchor = plan.Anchor;
        // Only singleton bindings may anchor or constrain another occurrence of an entity.
        foreach (var node in shape.Nodes.SelectMany(nodes => nodes))
        {
            if (node.Variable is not { } name || !input.TryGetValue(name, out var value)) { continue; }
            if (value is null) { yield break; }
            if (value is not GraphNode) { throw new GraphPlanningException("CG02013", ErrorMessages.EntityBindingRequired); }
        }
        foreach (var segment in shape.Segments.Where(segment => segment.Quantifier is null))
        {
            if (segment.Body.Edge?.Variable is not { } name || !input.TryGetValue(name, out var value)) { continue; }
            if (value is null) { yield break; }
            if (value is not GraphRelationship) { throw new GraphPlanningException("CG02013", ErrorMessages.EntityBindingRequired); }
        }
        IAsyncEnumerable<StoredGraphNode> candidates;
        var anchorNode = shape.Nodes[anchor.NodeIndex].FirstOrDefault(node => node.Variable is { } name && input.ContainsKey(name));
        if (anchorNode?.Variable is { } variable && input[variable] is GraphNode bound)
        { candidates = StreamBoundNodesAsync(operation.Updates.FindNode(bound.Id.Value) is { } node ? [node] : [], budget, token); }
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
            candidates = StreamBoundNodesAsync(endpoints, budget, token);
        }
        else { candidates = operation.Updates.StreamAnchorAsync(anchor, budget, token); }

        await foreach (var candidate in candidates.ConfigureAwait(false))
        {
            token.ThrowIfCancellationRequested();
            var node = GraphDatabase.Materialize(candidate);
            using var anchorMemory = budget.Memory.Reserve(GraphValueMemory.Node(node, token));
            PathTask? left = null;
            // Prepend continuations in reverse execution order: right, then left from the anchor.
            for (int i = 0; i < anchor.NodeIndex; i++)
            { left = SegmentTasks(shape.Segments[i], false, new NodeTask(shape.Nodes[i], left)); }
            PathTask? tasks = new StartLeftTask(node, left);
            for (int i = shape.Segments.Count - 1; i >= anchor.NodeIndex; i--)
            { tasks = SegmentTasks(shape.Segments[i], true, new NodeTask(shape.Nodes[i + 1], tasks)); }
            using var frontier = new PathFrontier(budget.Memory);
            frontier.Push(new PathState(node, new(input, StringComparer.Ordinal), [node], [], [], new(StringComparer.Ordinal), null,
                new NodeTask(shape.Nodes[anchor.NodeIndex], tasks)));
            var incidents = new Dictionary<IAsyncEnumerator<StoredGraphRelationship>, IncidentTask>();
            try
            {
                while (frontier.Count != 0)
                {
                    budget.OnFrontier?.Invoke(frontier.Count);
                    var state = frontier.Pop(out long stateBytes);
                    try
                    {
                    Dispatch:
                    token.ThrowIfCancellationRequested();
                    operation.EnsureActive();
                    switch (state.Task)
                    {
                        case null:
                            if (plan.Pattern.Variable is { } pathVariable)
                            { state.Bindings.Add(pathVariable, new GraphPath(state.Nodes.AsReadOnly(), state.Relationships.AsReadOnly())); }
                            frontier.ResizeActive(state, ref stateBytes);
                            // Fixed chains retain the base matcher's source-ordered nodes, then edges,
                            // independently of the anchor. Repetitions retain reverse encounter order.
                            var finalProperties = shape.HasQuantifier ? state.Properties : FixedProperties(shape, state);
                            bool matches = true;
                            for (var check = finalProperties; check is not null; check = check.Previous)
                            { if (!PropertiesMatch(check.Expected, check.Actual, state.Bindings, operation.EvaluationContext, parameters, deferUnbound)) { matches = false; break; } }
                            if (matches) { yield return (state.Bindings, finalProperties); }
                            break;
                        case StartLeftTask start:
                            state = state with { Current = start.Anchor, Task = start.Next };
                            goto Dispatch;
                        case NodeTask nodes:
                            bool accepted = true;
                            var properties = state.Properties;
                            foreach (var pattern in nodes.Patterns)
                            {
                                if (!AcceptNode(pattern, state.Current, state.Bindings, operation.EvaluationContext, parameters, deferUnbound: true)) { accepted = false; break; }
                                if (shape.HasQuantifier) { properties = new MatchedProperties(pattern.Properties, state.Current.Properties, properties); }
                            }
                            if (accepted)
                            {
                                state = state with { Properties = properties, Task = nodes.Next };
                                frontier.ResizeActive(state, ref stateBytes);
                                goto Dispatch;
                            }
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
                            var incident = operation.Updates.StreamIncidentAsync(state.Current.Id.Value,
                                GraphLabelEvaluator.IncidentDirection(edgeTask.Pattern, edgeTask.Forward),
                                operation.Updates.SelectTypes(edgeTask.Pattern), token, budget).GetAsyncEnumerator(token);
                            var incidentTask = new IncidentTask(edgeTask, incident);
                            incidents.Add(incident, incidentTask);
                            frontier.Push(state with { Task = incidentTask });
                            break;
                        case IncidentTask cursor:
                            cursor.ReleaseTarget();
                            // Resume the parent cursor after exactly one child. Sibling fan-out never
                            // retains copies of bindings, trails, identity sets or groups in the frontier.
                            if (!await cursor.Edges.MoveNextAsync().ConfigureAwait(false))
                            {
                                incidents.Remove(cursor.Edges);
                                await cursor.Edges.DisposeAsync().ConfigureAwait(false);
                                break;
                            }
                            frontier.Push(state);
                            var edge = cursor.Edges.Current;
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
                            if (state.Relationships.Count == 64) { throw new GraphPlanningException("CG05004", ErrorMessages.PathHopLimitExceeded); }
                            ulong next = edge.SourceId == from ? edge.TargetId : edge.SourceId;
                            if (operation.Updates.FindNode(next) is not { } target) { break; }
                            var nextNode = GraphDatabase.Materialize(target);
                            if (hop.Next is NodeTask targetNodes && targetNodes.Patterns.Any(pattern =>
                                !AcceptNode(pattern, nextNode, bindings, operation.EvaluationContext, parameters, deferUnbound: true))) { break; }
                            budget.ConsumeViable();
                            var copy = state.Fork(bindings);
                            cursor.TargetMemory = budget.Memory.Reserve(GraphValueMemory.Node(nextNode, token));
                            copy.Used.Add(edge.Id);
                            if (hop.Forward) { copy.Nodes.Add(nextNode); copy.Relationships.Add(relationship); }
                            else { copy.Nodes.Insert(0, nextNode); copy.Relationships.Insert(0, relationship); }
                            frontier.Push(copy with { Current = nextNode, Task = hop.Next,
                                Properties = shape.HasQuantifier
                                    ? new MatchedProperties(hop.Pattern.Properties, relationship.Properties, copy.Properties) : copy.Properties });
                            break;
                    }
                    }
                    finally { frontier.Release(stateBytes); }
                }
            }
            finally
            {
                // Parent incident cursors remain in the DFS frontier while a child runs.
                // Budget refusal, cancellation and evaluation/storage errors close them all.
                try { foreach (var incident in incidents.Values) { incident.ReleaseTarget(); } }
                finally { await GraphUpdates.DisposeIncidentStreamsAsync(incidents.Keys).ConfigureAwait(false); }
            }
        }
    }

    private static async IAsyncEnumerable<StoredGraphNode> StreamBoundNodesAsync(IEnumerable<StoredGraphNode> nodes,
        ExpansionBudget budget, [EnumeratorCancellation] CancellationToken token)
    {
        foreach (var node in nodes) { token.ThrowIfCancellationRequested(); budget.Consume(); yield return node; }
        await Task.CompletedTask.ConfigureAwait(false);
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

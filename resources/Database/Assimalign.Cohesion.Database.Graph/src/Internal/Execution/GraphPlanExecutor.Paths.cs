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
    private record MatchedProperties(IReadOnlyDictionary<string, GqlExpression> Expected,
        IReadOnlyDictionary<string, object?> Actual, MatchedProperties? Previous);
    private sealed record ScopedProperties : MatchedProperties
    {
        public ScopedProperties(MatchedProperties properties, MatchedProperties? previous, IReadOnlyDictionary<string, object?>? locals)
            : base(properties.Expected, properties.Actual, previous) { Locals = locals; }
        public IReadOnlyDictionary<string, object?>? Locals { get; }
    }
    private sealed record MatchedPredicate(GqlExpression Expression, MatchedPredicate? Previous,
        IReadOnlyDictionary<string, object?>? Locals = null);

    // Continuations and the DFS frontier live on the heap. A long trail never grows the CLR stack.
    private abstract record PathTask;
    private sealed record NodeTask(IReadOnlyList<GqlNodePattern> Patterns, PathTask? Next) : PathTask;
    private sealed record EdgeTask(GqlRelationshipPattern Pattern, bool Forward, PathTask? Next,
        StoredGraphRelationship? Seed = null) : PathTask;
    [global::System.Diagnostics.DebuggerDisplay("IncidentTask")]
    [global::System.Diagnostics.DebuggerTypeProxy(typeof(global::Assimalign.Cohesion.Database.Graph.Internal.GraphPlanExecutor.IncidentTask.DebugView))]
    private sealed record IncidentTask(EdgeTask Hop, IAsyncEnumerator<StoredGraphRelationship> Edges) : PathTask
    {
        internal GraphMemoryLease? TargetMemory { get; set; }
        internal void ReleaseTarget()
        { var memory = TargetMemory; TargetMemory = null; memory?.Dispose(); }

        private sealed class DebugView
        {
            private readonly global::Assimalign.Cohesion.Database.Graph.Internal.GraphPlanExecutor.IncidentTask _target;

            public DebugView(global::Assimalign.Cohesion.Database.Graph.Internal.GraphPlanExecutor.IncidentTask value)
            {
                _target = value;
            }

            public global::Assimalign.Cohesion.Database.Graph.Internal.GraphPlanExecutor.EdgeTask Hop => _target.Hop;
            public global::System.Collections.Generic.IAsyncEnumerator<global::Assimalign.Cohesion.Database.Graph.Storage.StoredGraphRelationship> Edges => _target.Edges;
        }
    }
    private sealed record RepeatTask(GraphPathSegment Segment, bool Forward, int Count, string[] Names, PathTask? Next) : PathTask;
    private record EndIterationTask(RepeatTask Repeat, PathTask? Next) : PathTask;
    private sealed record FilteredIterationTask(RepeatTask Repeat, PathTask? Next,
        MatchedProperties? Properties, MatchedPredicate? Predicates) : EndIterationTask(Repeat, Next);
    private sealed record PredicateTask(IReadOnlyList<GraphPathPrefilter> Filters, PathTask? Next) : PathTask;
    private sealed record StartLeftTask(GraphNode Anchor, PathTask? Next) : PathTask;

    private record PathState(GraphNode Current, Dictionary<string, object?> Bindings,
        List<GraphNode> Nodes, List<GraphRelationship> Relationships, HashSet<ulong> Used,
        Dictionary<string, object?[]> Groups, MatchedProperties? Properties, PathTask? Task)
    {
        internal PathState Fork(Dictionary<string, object?>? bindings = null) => this with
        {
            Bindings = bindings ?? new(Bindings, StringComparer.Ordinal), Nodes = new(Nodes), Relationships = new(Relationships),
            Used = new(Used), Groups = new(Groups, StringComparer.Ordinal),
        };
    }

    private sealed record FilteredPathState : PathState
    {
        public FilteredPathState(PathState state) : base(state) { }
        public MatchedPredicate? Predicates { get; init; }
    }

    private static MatchedPredicate? Predicates(PathState state) => (state as FilteredPathState)?.Predicates;

    private static PathState WithPredicates(PathState state, MatchedPredicate? predicates)
        => state is FilteredPathState filtered ? filtered with { Predicates = predicates } : state;

    [global::System.Diagnostics.DebuggerDisplay("PathFrontier")]
    [global::System.Diagnostics.DebuggerTypeProxy(typeof(global::Assimalign.Cohesion.Database.Graph.Internal.GraphPlanExecutor.PathFrontier.DebugView))]
    private sealed class PathFrontier : IDisposable
    {
        private readonly Stack<(PathState State, long Bytes)> _states = new();
        private readonly GraphMemoryOwner _memory;

        internal PathFrontier(GraphQueryMemory memory)
        {
            _memory = new GraphMemoryOwner(memory);
        }
        internal int Count => _states.Count;
        internal void ReserveActive(long bytes, ref long activeBytes)
        { _memory.Add(bytes); activeBytes += bytes; }
        internal void ReserveTasks(long bytes) => _memory.Add(bytes);
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
            // Named paths retain their GraphPath and two read-only collection wrappers.
            // Node/relationship buffers are already covered by the state's inline accounting.
            foreach (var binding in state.Bindings) { if (binding.Value is GraphPath) { bytes += 128; } }
            foreach (var values in state.Groups.Values) { bytes += 32 + 16L * values.Length; }
            for (var check = Predicates(state); check is not null; check = check.Previous)
            { bytes += 64 + (check.Locals is null ? 0 : 128 + 80L * check.Locals.Count); }
            for (var check = state.Properties; check is not null; check = check.Previous)
            { bytes += 64 + (check is ScopedProperties { Locals: { } locals } ? 128 + 80L * locals.Count : 0); }
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

        private sealed class DebugView
        {
            public DebugView(global::Assimalign.Cohesion.Database.Graph.Internal.GraphPlanExecutor.PathFrontier value)
            {
                _ = value;
            }
        }
    }

    private static async IAsyncEnumerable<PathMatch> SearchPathStreamAsync(
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
        IAsyncEnumerable<StoredGraphNode>? candidates;
        var anchorNode = shape.Nodes[anchor.NodeIndex].FirstOrDefault(node => node.Variable is { } name && input.ContainsKey(name));
        if (plan is PathSearch)
        { candidates = StreamBoundNodesAsync(operation.Updates.FindNode(((PathSearch)plan).Start) is { } seed ? [seed] : [], budget, token); }
        else if (anchorNode?.Variable is { } variable && input[variable] is GraphNode bound)
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
        else { candidates = anchor.RelationshipTypes is null ? operation.Updates.StreamAnchorAsync(anchor, budget, token) : null; }

        await foreach (var candidate in StreamPathSeedsAsync(operation, anchor, shape, candidates, budget, token).ConfigureAwait(false))
        {
            token.ThrowIfCancellationRequested();
            var node = GraphDatabase.Materialize(candidate.Node);
            using var anchorMemory = budget.Memory.Reserve(GraphValueMemory.Node(node, token));
            // Continuations for nullable segments can outnumber the 64 actual hops.
            // Reserve their retained chain before constructing it for a search.
            using var frontier = new PathFrontier(budget.Memory);
            if (plan is PathSearch || shape.HasPrefilters)
            { frontier.ReserveTasks(128L * (shape.Nodes.Count + shape.Segments.Count)); }
            PathTask? left = null;
            // Prepend continuations in reverse execution order: right, then left from the anchor.
            for (int i = 0; i < anchor.NodeIndex; i++)
            { left = SegmentTasks(shape.Segments[i], false, new NodeTask(shape.Nodes[i], left)); }
            PathTask? tasks = new StartLeftTask(node, left);
            for (int i = shape.Segments.Count - 1; i >= anchor.NodeIndex; i--)
            {
                var next = new NodeTask(shape.Nodes[i + 1], tasks);
                tasks = i == anchor.NodeIndex && candidate.Edge is { } seed
                    ? new EdgeTask(shape.Segments[i].Body.Edge!, true, next, seed)
                    : SegmentTasks(shape.Segments[i], true, next);
            }
            var initial = new PathState(node, new(input, StringComparer.Ordinal), [node], [], [], new(StringComparer.Ordinal), null,
                new NodeTask(shape.Nodes[anchor.NodeIndex], tasks));
            frontier.Push(plan is PathSearch || shape.HasPrefilters ? new FilteredPathState(initial) : initial);
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
                    if (plan is PathSearch) { budget.Consume(); budget.SelectorStates++; }
                    switch (state.Task)
                    {
                        case null:
                            if (plan is PathSearch && (state.Nodes[^1].Id.Value != ((PathSearch)plan).End ||
                                ((PathSearch)plan).ExactLength && state.Relationships.Count != ((PathSearch)plan).HopLimit)) { break; }
                            if (plan.Pattern.Variable is { } pathVariable)
                            {
                                if (state.Bindings.TryGetValue(pathVariable, out var existingPath))
                                {
                                    if (existingPath is not GraphPath boundPath || !SamePath(boundPath, state.Nodes, state.Relationships)) { break; }
                                }
                                else
                                {
                                    frontier.ReserveActive(128, ref stateBytes);
                                    state.Bindings.Add(pathVariable, new GraphPath(state.Nodes.AsReadOnly(), state.Relationships.AsReadOnly()));
                                }
                            }
                            frontier.ResizeActive(state, ref stateBytes);
                            // Fixed chains retain the base matcher's source-ordered nodes, then edges,
                            // independently of the anchor. Repetitions retain reverse encounter order.
                            var finalProperties = shape.HasQuantifier ? state.Properties : FixedProperties(shape, state);
                            if (!shape.HasQuantifier && (plan is PathSearch || shape.HasPrefilters))
                            {
                                state = state with { Properties = finalProperties };
                                frontier.ResizeActive(state, ref stateBytes);
                            }
                            bool matches = CheckProperties(finalProperties, null, state.Bindings, operation, parameters, deferUnbound);
                            matches &= CheckPredicates(Predicates(state), null, state.Bindings, operation, parameters);
                            foreach (var filter in shape.Prefilters)
                            { if (!PredicateMatches(filter.Predicate, state.Bindings, operation, parameters)) { matches = false; break; } }
                            if (matches)
                            {
                                var path = plan.Pattern.Variable is { } name ? (GraphPath)state.Bindings[name]!
                                    : plan is not PathSearch ? null : new GraphPath(state.Nodes.AsReadOnly(), state.Relationships.AsReadOnly());
                                yield return new PathMatch(state.Bindings, plan is not PathSearch ? finalProperties : path);
                            }
                            break;
                        case StartLeftTask start:
                            state = state with { Current = start.Anchor, Task = start.Next };
                            goto Dispatch;
                        case NodeTask nodes:
                            bool accepted = true;
                            var properties = state.Properties;
                            var nodePredicates = Predicates(state);
                            foreach (var pattern in nodes.Patterns)
                            {
                                if (!AcceptNode(pattern, state.Current, state.Bindings, operation.EvaluationContext, parameters, deferUnbound: true)) { accepted = false; break; }
                                if (pattern.Predicate is { } predicate)
                                {
                                    if (!GraphValueExpressions.ReferencesUnbound(predicate, state.Bindings))
                                    { if (!PredicateMatches(predicate, state.Bindings, operation, parameters)) { accepted = false; break; } }
                                    else { nodePredicates = new MatchedPredicate(predicate, nodePredicates); }
                                }
                                if (shape.HasQuantifier) { properties = new MatchedProperties(pattern.Properties, state.Current.Properties, properties); }
                            }
                            if (accepted)
                            {
                                state = WithPredicates(state with { Properties = properties, Task = nodes.Next }, nodePredicates);
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
                                EndIterationTask end = state is FilteredPathState filtered
                                    ? new FilteredIterationTask(repeat, repeat.Next, state.Properties, filtered.Predicates)
                                    : new EndIterationTask(repeat, repeat.Next);
                                frontier.Push(iteration with { Task = BodyTasks(repeat.Segment.Body, repeat.Forward, end) });
                            }
                            if (repeat.Count >= (repeat.Segment.Quantifier?.Min ?? 1))
                            { frontier.Push(state with { Task = repeat.Next }); }
                            break;
                        case PredicateTask filter:
                            state = WithPredicates(state with { Task = filter.Next }, AddPredicates(filter.Filters, Predicates(state)));
                            frontier.ResizeActive(state, ref stateBytes);
                            goto Dispatch;
                        case EndIterationTask end:
                            // Repeated-body predicates see this iteration's singleton bindings.
                            if (end is FilteredIterationTask filteredEnd && !CheckIteration(ref state, filteredEnd, operation, parameters)) { break; }
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
                            // Once SIMPLE closes its two endpoints, no side may extend it.
                            // The same rule works when the right side runs before the left.
                            if (plan.Mode == GqlPathMode.Simple && state.Nodes.Count > 1 && state.Nodes[0].Id == state.Nodes[^1].Id)
                            { break; }
                            if (plan is BoundPathSearch boundSearch)
                            {
                                if (state.Relationships.Count >= boundSearch.Value.Relationships.Count) { break; }
                                budget.Consume();
                                if (operation.Updates.FindRelationship(boundSearch.Value.Relationships[state.Relationships.Count].Id.Value) is not { } boundEdge) { break; }
                                edgeTask = edgeTask with { Seed = boundEdge };
                            }
                            else if (operation.FunctionBindings is GraphCorrelatedBindings && edgeTask.Seed is null && edgeTask.Pattern.Variable is { } argument &&
                                state.Bindings.TryGetValue(argument, out var argumentValue) && argumentValue is GraphRelationship argumentEdge)
                            {
                                budget.Consume();
                                if (operation.Updates.FindRelationship(argumentEdge.Id.Value) is not { } boundEdge) { break; }
                                edgeTask = edgeTask with { Seed = boundEdge };
                            }
                            var incident = (edgeTask.Seed is { } seedEdge ? StreamSeedEdgeAsync(seedEdge, token)
                                : operation.Updates.StreamIncidentAsync(state.Current.Id.Value,
                                GraphLabelEvaluator.IncidentDirection(edgeTask.Pattern, edgeTask.Forward),
                                operation.Updates.SelectTypes(edgeTask.Pattern), token, budget)).GetAsyncEnumerator(token);
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
                            if (plan.Mode == GqlPathMode.Trail && state.Used.Contains(edge.Id) || !GraphLabelEvaluator.Accepts(hop.Pattern, edge.Type) ||
                                outgoing && edge.SourceId != from || incoming && edge.TargetId != from) { break; }
                            ulong next = edge.SourceId == from ? edge.TargetId : edge.SourceId;
                            if (plan is PathSearch { Identity: { } identity } && state.Relationships.Count < identity.Count &&
                                edge.Id != identity[state.Relationships.Count]) { break; }
                            if (plan is PathSearch && state.Relationships.Count < ((PathSearch)plan).HopLimit)
                            {
                                int remaining = ((PathSearch)plan).HopLimit - state.Relationships.Count - 1;
                                if (((PathSearch)plan).Distances is { } distances && (!distances.TryGetValue(next, out int distance) || distance != remaining) ||
                                    ((PathSearch)plan).Layers is { } layers && !layers[remaining].Contains(next)) { break; }
                            }
                            // Nodes is the accounted, source-ordered identity state (at most 65
                            // entries). SIMPLE can repeat only the opposite endpoint, then stops.
                            if (plan.Mode is GqlPathMode.Acyclic or GqlPathMode.Simple && ContainsNodeIdentity(state.Nodes, next) &&
                                (plan.Mode == GqlPathMode.Acyclic || next != (hop.Forward ? state.Nodes[0] : state.Nodes[^1]).Id.Value))
                            { break; }
                            var bindings = new Dictionary<string, object?>(state.Bindings, StringComparer.Ordinal);
                            var relationship = GraphDatabase.Materialize(edge);
                            if (!Accept(hop.Pattern.Variable, relationship, bindings) ||
                                !PropertiesMatch(hop.Pattern.Properties, relationship.Properties, bindings, operation.EvaluationContext, parameters, deferUnbound: true)) { break; }
                            var edgePredicates = Predicates(state);
                            if (hop.Pattern.Predicate is { } edgePredicate)
                            {
                                if (!GraphValueExpressions.ReferencesUnbound(edgePredicate, bindings))
                                { if (!PredicateMatches(edgePredicate, bindings, operation, parameters)) { break; } }
                                else { edgePredicates = new MatchedPredicate(edgePredicate, edgePredicates); }
                            }
                            if (plan is PathSearch && state.Relationships.Count == ((PathSearch)plan).HopLimit)
                            {
                                var search = (PathSearch)plan;
                                search.Truncated = true;
                                if (search.FirstTruncatedPrefix is null || CompareExtendedPrefix(state.Relationships, edge.Id, search.FirstTruncatedPrefix) < 0)
                                { search.FirstTruncatedPrefix = state.Relationships.Select(item => item.Id.Value).Append(edge.Id).ToArray(); }
                                break;
                            }
                            // The hop's own checks precede the cap; deferred references still qualify.
                            // A qualifying 65th edge fails before any target constraints can hide it.
                            if (state.Relationships.Count == 64) { throw new GraphPlanningException("CG05004", ErrorMessages.PathHopLimitExceeded); }
                            if (operation.Updates.FindNode(next) is not { } target) { break; }
                            var nextNode = GraphDatabase.Materialize(target);
                            if (hop.Next is NodeTask targetNodes && targetNodes.Patterns.Any(pattern =>
                                !AcceptNode(pattern, nextNode, bindings, operation.EvaluationContext, parameters, deferUnbound: true))) { break; }
                            budget.ConsumeViable();
                            var copy = state.Fork(bindings);
                            cursor.TargetMemory = budget.Memory.Reserve(GraphValueMemory.Node(nextNode, token));
                            if (plan.Mode == GqlPathMode.Trail) { copy.Used.Add(edge.Id); }
                            if (hop.Forward) { copy.Nodes.Add(nextNode); copy.Relationships.Add(relationship); }
                            else { copy.Nodes.Insert(0, nextNode); copy.Relationships.Insert(0, relationship); }
                            frontier.Push(WithPredicates(copy with { Current = nextNode, Task = hop.Next,
                                Properties = shape.HasQuantifier
                                    ? new MatchedProperties(hop.Pattern.Properties, relationship.Properties, copy.Properties) : copy.Properties }, edgePredicates));
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

    private static async IAsyncEnumerable<(StoredGraphNode Node, StoredGraphRelationship? Edge)> StreamPathSeedsAsync(
        GraphOperation operation, GraphAnchor anchor, GraphPathShape shape, IAsyncEnumerable<StoredGraphNode>? nodes,
        ExpansionBudget budget, [EnumeratorCancellation] CancellationToken token)
    {
        if (nodes is not null)
        {
            await foreach (var node in nodes.ConfigureAwait(false)) { yield return (node, null); }
            yield break;
        }
        var direction = shape.Segments[anchor.NodeIndex].Body.Edge!.Direction;
        await foreach (var edge in operation.Updates.StreamTypeAnchorAsync(anchor.RelationshipTypes!, budget, token).ConfigureAwait(false))
        {
            token.ThrowIfCancellationRequested();
            ulong left = direction == GqlPatternDirection.Incoming ? edge.TargetId : edge.SourceId;
            if (operation.Updates.FindNode(left) is { } node) { yield return (node, edge); }
            if (direction is GqlPatternDirection.Undirected or GqlPatternDirection.LeftOrRight && edge.SourceId != edge.TargetId &&
                operation.Updates.FindNode(edge.TargetId) is { } other)
            { yield return (other, edge); }
        }
    }

    private static async IAsyncEnumerable<StoredGraphRelationship> StreamSeedEdgeAsync(StoredGraphRelationship edge,
        [EnumeratorCancellation] CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        yield return edge; // Its posting/overlay visit already consumed the physical unit.
        await Task.CompletedTask.ConfigureAwait(false);
    }

    private static bool ContainsNodeIdentity(List<GraphNode> nodes, ulong id)
    {
        foreach (var node in nodes) { if (node.Id.Value == id) { return true; } }
        return false;
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
        PathTask? tasks = body.Prefilters.Count == 0 ? next : new PredicateTask(body.Prefilters, next);
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

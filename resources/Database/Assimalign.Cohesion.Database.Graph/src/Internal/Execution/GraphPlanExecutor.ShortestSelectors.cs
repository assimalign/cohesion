using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Runtime.ExceptionServices;
using System.Threading;
using System.Threading.Tasks;

using Assimalign.Cohesion.Database.Graph.Language;
using Assimalign.Cohesion.Database.Graph.Protocol;
using Assimalign.Cohesion.Database.Graph.Storage;

namespace Assimalign.Cohesion.Database.Graph.Internal;

internal static partial class GraphPlanExecutor
{
    private sealed record LocalPathStep(GqlRelationshipPattern Edge, IReadOnlyList<GqlNodePattern> From, IReadOnlyList<GqlNodePattern> To);
    private sealed record LocalPathSearch(IReadOnlyList<LocalPathStep> Steps, int Minimum, int Maximum, bool MatchIdentity = false);

    private static LocalPathSearch? LocalStep(GraphPathShape shape, Dictionary<string, object?> input,
        GraphMemoryOwner memory, CancellationToken token)
    {
        // Fixed chains and one repetition of a fixed body have one decomposition per
        // physical length. Adjacent quantifiers instead validate one physical identity at a time.
        if (shape.PathPrefilterCount != 0 || shape.Segments.Count == 0 || shape.Bound == 0 && !shape.HasUnboundedQuantifier) { return null; }
        var repeated = shape.Segments.Count == 1 ? shape.Segments[0].Quantifier : null;
        var body = repeated is null ? shape : shape.Segments[0].Body;
        if (body.Edge is null && body.Segments.Any(segment => segment.Quantifier is not null || segment.Body.Edge is null))
        { return AmbiguousLocalSearch(shape, input, memory, token); }
        memory.Add(512 + 80L * (input.Count + shape.Bindings().Count()) + 96L * body.Segments.Count);
        var steps = new List<LocalPathStep>();
        if (body.Edge is { } repeatedEdge) { steps.Add(new LocalPathStep(repeatedEdge, [], [])); }
        else
        {
            for (int i = 0; i < body.Segments.Count; i++)
            { steps.Add(new LocalPathStep(body.Segments[i].Body.Edge!, body.Nodes[i], body.Nodes[i + 1])); }
        }
        var scope = new Dictionary<string, object?>(input, StringComparer.Ordinal);
        var boundaries = shape.Nodes[0].Concat(shape.Nodes[^1]);
        foreach (var node in boundaries)
        { if (node.Variable is { } name) { scope[name] = null; } }
        foreach (var node in boundaries)
        { if (!Local(node.Properties, node.Predicate)) { return null; } }
        var endpoints = scope;
        foreach (var step in steps)
        {
            scope = new Dictionary<string, object?>(endpoints, StringComparer.Ordinal);
            foreach (var node in step.From.Concat(step.To))
            { if (node.Variable is { } name) { scope[name] = null; } }
            if (step.Edge.Variable is { } edgeName) { scope[edgeName] = null; }
            foreach (var node in step.From.Concat(step.To))
            { if (!Local(node.Properties, node.Predicate)) { return null; } }
            if (!Local(step.Edge.Properties, step.Edge.Predicate)) { return null; }
        }
        return new LocalPathSearch(steps, (repeated?.Min ?? 1) * steps.Count,
            repeated is null ? steps.Count : repeated.Max is { } max ? max * steps.Count : 64);

        bool Local(IReadOnlyDictionary<string, GqlExpression> properties, GqlExpression? predicate)
        {
            token.ThrowIfCancellationRequested();
            return properties.Values.All(value => !GraphValueExpressions.ReferencesUnbound(value, scope)) &&
                (predicate is null || !GraphValueExpressions.ReferencesUnbound(predicate, scope));
        }
    }

    private static async IAsyncEnumerable<PathMatch> SelectLocalShortestAsync(GraphDatabase database,
        GraphOperation operation, GraphPathPlan plan, Dictionary<string, object?> input, LocalPathSearch local,
        StoredGraphNode start, StoredGraphNode target, GraphIncidentDirection forwardDirection, IReadOnlyCollection<Guid>? types, ExpansionBudget budget,
        IReadOnlyDictionary<string, object?>? parameters, [EnumeratorCancellation] CancellationToken token)
    {
        using var memory = new GraphMemoryOwner(budget.Memory);
        memory.Add(1024 + 80L * (input.Count + plan.Shape!.Bindings().Count()));
        var endpoints = new Dictionary<string, object?>(input, StringComparer.Ordinal);
        var first = GraphDatabase.Materialize(start);
        var last = GraphDatabase.Materialize(target);
        memory.RetainValue(first, token); memory.RetainValue(last, token);
        if (!Bind(plan.Shape.Nodes[0], first) || !Bind(plan.Shape.Nodes[^1], last) ||
            !Check(plan.Shape.Nodes[0], first) || !Check(plan.Shape.Nodes[^1], last)) { yield break; }

        // Reverse BFS retains vertices and accepted transitions, never path histories or
        // predecessor ties. Distances describe the shortest-path DAG used by lazy DFS below.
        var distances = new Dictionary<ulong, int>();
        var predecessors = new Dictionary<ulong, List<ulong>>();
        var forward = local.MatchIdentity ? new Dictionary<ulong, List<(ulong Node, ulong Edge)>>() : null;
        var pending = new Queue<ulong>();
        Add(target.Id, 0);
        var direction = forwardDirection == GraphIncidentDirection.Outgoing ? GraphIncidentDirection.Incoming
            : forwardDirection == GraphIncidentDirection.Incoming ? GraphIncidentDirection.Outgoing : GraphIncidentDirection.Both;
        while (pending.TryDequeue(out ulong current))
        {
            token.ThrowIfCancellationRequested(); operation.EnsureActive();
            budget.Consume(); budget.SelectorStates++;
            var incoming = new List<ulong>();
            predecessors.Add(current, incoming);
            await foreach (var edge in operation.Updates.StreamIncidentAsync(current, direction, types, token, budget).ConfigureAwait(false))
            {
                token.ThrowIfCancellationRequested(); operation.EnsureActive();
                ulong previous = edge.SourceId == current ? edge.TargetId : edge.SourceId;
                if (!local.Steps.Any(step => LocalStepMatches(step, previous, current, edge, endpoints, operation, budget, parameters, token))) { continue; }
                budget.ConsumeViable();
                memory.Add(32); // Transition identity, list growth and its backing storage.
                incoming.Add(previous);
                if (forward is not null)
                {
                    if (!forward.TryGetValue(previous, out var outgoing))
                    { memory.Add(128); forward.Add(previous, outgoing = []); }
                    memory.Add(48); outgoing.Add((current, edge.Id));
                }
                if (!distances.ContainsKey(previous)) { Add(previous, distances[current] + 1); }
            }
        }
        if (!distances.TryGetValue(start.Id, out int shortest)) { yield break; }
        if (shortest > local.Maximum)
        {
            if (plan.Shape.HasUnboundedQuantifier) { throw new GraphPlanningException("CG05004", ErrorMessages.PathHopLimitExceeded); }
            yield break;
        }
        if (forward is not null)
        {
            foreach (var edges in forward.Values)
            {
                try
                {
                    edges.Sort((left, right) =>
                    {
                        token.ThrowIfCancellationRequested(); operation.EnsureActive(); budget.Consume();
                        int order = left.Edge.CompareTo(right.Edge);
                        return order != 0 ? order : left.Node.CompareTo(right.Node);
                    });
                }
                catch (InvalidOperationException error) when (error.InnerException is { } cause)
                { ExceptionDispatchInfo.Capture(cause).Throw(); throw; }
            }
        }

        long remaining = plan.Selection!.Count!.Value;
        var layers = new List<HashSet<ulong>>();
        for (int length = Math.Max(shortest, local.Minimum); length <= local.Maximum; length++)
        {
            bool shortestDag = length == shortest;
            if (!shortestDag)
            {
                // For longer lengths (or an unmet minimum), exact walk reachability is
                // only a pruning condition. The matcher still enforces the full mode history.
                if (layers.Count == 0) { AddLayer([target.Id]); }
                while (layers.Count <= length)
                {
                    var next = new HashSet<ulong>();
                    memory.Add(128);
                    foreach (ulong end in layers[^1])
                    {
                        foreach (ulong previous in predecessors[end])
                        {
                            token.ThrowIfCancellationRequested(); operation.EnsureActive(); budget.Consume();
                            if (next.Contains(previous)) { continue; }
                            memory.Add(96); next.Add(previous);
                        }
                    }
                    layers.Add(next);
                    if (next.Count == 0) { yield break; }
                }
            }
            bool truncated = false;
            IEnumerable<ulong[]?> identities;
            if (forward is null) { identities = [null]; }
            else { identities = PhysicalIdentities(forward, start.Id, target.Id,
                length, plan.Mode, shortestDag ? distances : null, shortestDag ? null : layers, operation, budget, token); }
            // A fixed chain or single fixed-body repetition has one decomposition. Ordered incident
            // cursors plus depth-first traversal produce complete relationship sequences in
            // identity order; a fixed start and relationship sequence also fix the node sequence.
            foreach (var identity in identities)
            {
                var search = new PathSearch
                {
                    Start = start.Id, End = target.Id, HopLimit = length, ExactLength = true, Identity = identity,
                    Distances = shortestDag ? distances : null, Layers = shortestDag ? null : layers,
                };
                await foreach (var match in SearchPathStreamAsync(database, operation, plan, input, budget, parameters,
                    token, search: search).ConfigureAwait(false))
                {
                    budget.Consume(); budget.SelectorCandidates++;
                    budget.Consume(); budget.SelectorSelected++;
                    yield return match with { Properties = null };
                    if (--remaining == 0) { yield break; }
                    if (identity is not null) { break; } // First source-ordered decomposition owns the bindings.
                }
                truncated |= search.Truncated;
            }
            if (length == local.Maximum && (truncated || forward is not null && plan.Shape.HasUnboundedQuantifier &&
                PhysicalIdentities(forward, start.Id, null, 65, plan.Mode, null, null, operation, budget, token).Any()))
            { throw new GraphPlanningException("CG05004", ErrorMessages.PathHopLimitExceeded); }
        }

        void Add(ulong identity, int distance)
        {
            token.ThrowIfCancellationRequested(); budget.Consume();
            memory.Add(384); // Distance and transition dictionaries, list header, queue and growth.
            distances.Add(identity, distance); pending.Enqueue(identity);
        }
        void AddLayer(HashSet<ulong> layer)
        { memory.Add(128 + 96L * layer.Count); layers.Add(layer); }
        bool Bind(IReadOnlyList<GqlNodePattern> patterns, GraphNode node)
            => patterns.All(pattern => GraphLabelEvaluator.Accepts(pattern, node.Labels) && Accept(pattern.Variable, node, endpoints));
        bool Check(IReadOnlyList<GqlNodePattern> patterns, GraphNode node)
            => patterns.All(pattern => PropertiesMatch(pattern.Properties, node.Properties, endpoints, operation.EvaluationContext, parameters) &&
                (pattern.Predicate is null || PredicateMatches(pattern.Predicate, endpoints, operation, parameters)));
    }

    private static LocalPathSearch? AmbiguousLocalSearch(GraphPathShape shape, Dictionary<string, object?> input,
        GraphMemoryOwner memory, CancellationToken token)
    {
        memory.Add(512 + 128L * shape.Bindings().Count() + 80L * input.Count);
        var endpoints = new Dictionary<string, object?>(input, StringComparer.Ordinal);
        foreach (var node in shape.Nodes[0].Concat(shape.Nodes[^1]))
        { if (node.Variable is { } name) { endpoints[name] = null; } }
        var scope = endpoints;
        var steps = new List<LocalPathStep>();
        var pending = new Stack<(GraphPathShape Shape, long Minimum)>();
        pending.Push((shape, 1));
        long minimum = 0;
        while (pending.TryPop(out var item))
        {
            token.ThrowIfCancellationRequested();
            foreach (var node in item.Shape.Nodes.SelectMany(nodes => nodes))
            {
                scope = new Dictionary<string, object?>(endpoints, StringComparer.Ordinal);
                if (node.Variable is { } name) { scope[name] = null; }
                if (!Local(node.Properties, node.Predicate)) { return null; }
            }
            if (item.Shape.Edge is { } edge)
            {
                if (!Add(new LocalPathStep(edge, [], []))) { return null; }
                minimum += item.Minimum;
            }
            for (int i = item.Shape.Segments.Count - 1; i >= 0; i--)
            {
                var segment = item.Shape.Segments[i];
                if (segment.Quantifier is null && segment.Body.Edge is { } fixedEdge)
                {
                    if (!Add(new LocalPathStep(fixedEdge, item.Shape.Nodes[i], item.Shape.Nodes[i + 1]))) { return null; }
                    minimum += item.Minimum;
                }
                else { memory.Add(32); pending.Push((segment.Body, item.Minimum * (segment.Quantifier?.Min ?? 1))); }
            }
        }
        return new LocalPathSearch(steps, (int)minimum, shape.HasUnboundedQuantifier ? 64 : (int)shape.Bound, MatchIdentity: true);

        bool Add(LocalPathStep step)
        {
            scope = new Dictionary<string, object?>(endpoints, StringComparer.Ordinal);
            foreach (var node in step.From.Concat(step.To))
            { if (node.Variable is { } name) { scope[name] = null; } }
            if (step.Edge.Variable is { } edgeName) { scope[edgeName] = null; }
            if (!Local(step.Edge.Properties, step.Edge.Predicate)) { return false; }
            memory.Add(96); steps.Add(step); return true;
        }
        bool Local(IReadOnlyDictionary<string, GqlExpression> properties, GqlExpression? predicate)
            => properties.Values.All(value => !GraphValueExpressions.ReferencesUnbound(value, scope)) &&
                (predicate is null || !GraphValueExpressions.ReferencesUnbound(predicate, scope));
    }

    private static IEnumerable<ulong[]> PhysicalIdentities(Dictionary<ulong, List<(ulong Node, ulong Edge)>> forward,
        ulong start, ulong? target, int length, GqlPathMode mode, IReadOnlyDictionary<ulong, int>? distances,
        IReadOnlyList<HashSet<ulong>>? layers, GraphOperation operation, ExpansionBudget budget, CancellationToken token)
    {
        using var memory = budget.Memory.Reserve(256 + 32L * (length + 1));
        var nodes = new ulong[length + 1];
        var edges = new ulong[length];
        var cursors = new int[length + 1];
        nodes[0] = start;
        int depth = 0;
        while (depth >= 0)
        {
            token.ThrowIfCancellationRequested(); operation.EnsureActive(); budget.Consume(); budget.SelectorStates++;
            if (depth == length)
            {
                if (target is null || nodes[depth] == target) { yield return edges.ToArray(); }
                depth--; continue;
            }
            if (mode == GqlPathMode.Simple && depth > 0 && nodes[depth] == start ||
                !forward.TryGetValue(nodes[depth], out var next) || cursors[depth] == next.Count)
            { depth--; continue; }
            var hop = next[cursors[depth]++];
            int remaining = length - depth - 1;
            if (distances is not null && (!distances.TryGetValue(hop.Node, out int distance) || distance != remaining) ||
                layers is not null && !layers[remaining].Contains(hop.Node)) { continue; }
            bool accepted = true;
            for (int i = 0; i <= depth; i++)
            {
                if (mode == GqlPathMode.Trail && i < depth && edges[i] == hop.Edge ||
                    mode is GqlPathMode.Acyclic or GqlPathMode.Simple && nodes[i] == hop.Node &&
                    (mode == GqlPathMode.Acyclic || hop.Node != start)) { accepted = false; break; }
            }
            if (!accepted) { continue; }
            edges[depth] = hop.Edge; nodes[depth + 1] = hop.Node;
            depth++; cursors[depth] = 0;
        }
    }

    private static bool LocalStepMatches(LocalPathStep step, ulong from, ulong to, StoredGraphRelationship stored,
        Dictionary<string, object?> endpoints, GraphOperation operation, ExpansionBudget budget,
        IReadOnlyDictionary<string, object?>? parameters, CancellationToken token)
    {
        if (!GraphLabelEvaluator.Accepts(step.Edge, stored.Type) ||
            step.Edge.Direction == GqlPatternDirection.Outgoing && stored.SourceId != from ||
            step.Edge.Direction == GqlPatternDirection.Incoming && stored.TargetId != from) { return false; }
        if (step.From.Count == 0 && step.To.Count == 0 && step.Edge.Properties.Count == 0 && step.Edge.Predicate is null &&
            (step.Edge.Variable is null || !endpoints.ContainsKey(step.Edge.Variable))) { return true; }
        using var memory = new GraphMemoryOwner(budget.Memory);
        memory.Add(512 + 80L * (endpoints.Count + step.From.Count + step.To.Count + 1));
        budget.Consume(); budget.Consume();
        operation.EnsureActive();
        if (operation.Updates.FindNode(from) is not { } source || operation.Updates.FindNode(to) is not { } target) { return false; }
        operation.EnsureActive();
        var left = GraphDatabase.Materialize(source);
        var right = GraphDatabase.Materialize(target);
        var edge = GraphDatabase.Materialize(stored);
        memory.RetainValue(left, token); memory.RetainValue(right, token); memory.RetainValue(edge, token);
        var bindings = new Dictionary<string, object?>(endpoints, StringComparer.Ordinal);
        if (!Accept(step.Edge.Variable, edge, bindings) || !Bind(step.From, left) || !Bind(step.To, right)) { return false; }
        return Check(step.From, left) && Check(step.To, right) &&
            PropertiesMatch(step.Edge.Properties, edge.Properties, bindings, operation.EvaluationContext, parameters) &&
            (step.Edge.Predicate is null || PredicateMatches(step.Edge.Predicate, bindings, operation, parameters));

        bool Bind(IReadOnlyList<GqlNodePattern> patterns, GraphNode node)
            => patterns.All(pattern => GraphLabelEvaluator.Accepts(pattern, node.Labels) && Accept(pattern.Variable, node, bindings));
        bool Check(IReadOnlyList<GqlNodePattern> patterns, GraphNode node)
            => patterns.All(pattern => PropertiesMatch(pattern.Properties, node.Properties, bindings, operation.EvaluationContext, parameters) &&
                (pattern.Predicate is null || PredicateMatches(pattern.Predicate, bindings, operation, parameters)));
    }
}

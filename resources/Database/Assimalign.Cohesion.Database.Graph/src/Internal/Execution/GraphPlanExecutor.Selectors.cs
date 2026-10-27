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
    private readonly record struct PathMatch(Dictionary<string, object?> Bindings, MatchedProperties? Properties, GraphPath? Path, long Discovery = 0);

    private sealed class PathSearch
    {
        public ulong Start { get; init; }
        public ulong End { get; init; }
        public int HopLimit { get; init; }
        public bool ExactLength { get; init; }
        public bool Truncated { get; set; }
        public ulong[]? FirstTruncatedPrefix { get; set; }
    }

    private static IAsyncEnumerable<PathMatch> MatchCoreStreamAsync(
        GraphDatabase database, GraphOperation operation, GraphPathPlan plan, Dictionary<string, object?> input,
        ExpansionBudget budget, IReadOnlyDictionary<string, object?>? parameters,
        CancellationToken token, bool deferUnbound = false)
        => plan.Selection is null
            ? SearchPathStreamAsync(database, operation, plan, input, budget, parameters, token, deferUnbound)
            : SelectPathStreamAsync(database, operation, plan, input, budget, parameters, token);

    private static async IAsyncEnumerable<PathMatch> SelectPathStreamAsync(
        GraphDatabase database, GraphOperation operation, GraphPathPlan plan, Dictionary<string, object?> input,
        ExpansionBudget budget, IReadOnlyDictionary<string, object?>? parameters,
        [EnumeratorCancellation] CancellationToken token)
    {
        var selection = plan.Selection!;
        var shape = selection.Search;
        // Selected paths always search from their source boundary. An interior index
        // anchor must not change endpoint partitioning, identity order or local scopes.
        var searchPlan = plan with { Anchor = new GraphAnchor(0, null, null, null) };
        using var searchMemory = new GraphMemoryOwner(budget.Memory);
        searchMemory.Add(512);
        var connectivity = Connectivity(shape, operation, budget, searchMemory, token);
        var startAnchor = plan.Anchor.NodeIndex == 0 && plan.Anchor.RelationshipTypes is null
            ? plan.Anchor : searchPlan.Anchor;
        IAsyncEnumerable<StoredGraphNode> starts = operation.Updates.StreamAnchorAsync(startAnchor, budget, token);
        var boundStart = shape.Nodes[0].FirstOrDefault(node => node.Variable is { } name && input.ContainsKey(name));
        if (boundStart?.Variable is { } startName)
        {
            if (input[startName] is null) { yield break; }
            if (input[startName] is not GraphNode node)
            { throw new GraphPlanningException("CG02013", ErrorMessages.EntityBindingRequired); }
            starts = StreamBoundNodesAsync(operation.Updates.FindNode(node.Id.Value) is { } stored ? [stored] : [], budget, token);
        }
        var boundEnd = shape.Nodes[^1].FirstOrDefault(node => node.Variable is { } name && input.ContainsKey(name));
        ulong? endId = null;
        if (boundEnd?.Variable is { } endName)
        {
            if (input[endName] is null) { yield break; }
            if (input[endName] is not GraphNode node)
            { throw new GraphPlanningException("CG02013", ErrorMessages.EntityBindingRequired); }
            endId = node.Id.Value;
        }

        bool sameEndpoint = shape.Nodes[0].Any(left => left.Variable is { } name &&
            shape.Nodes[^1].Any(right => right.Variable == name));
        await foreach (var start in starts.ConfigureAwait(false))
        {
            token.ThrowIfCancellationRequested();
            // A finite topology pass eliminates disconnected pairs even for WALK.
            // It is only a necessary condition: pattern/type/predicate and history
            // restrictions remain in the accepting search, never a vertex-visited BFS.
            using var componentMemory = new GraphMemoryOwner(budget.Memory);
            ulong? pairEnd = sameEndpoint ? start.Id : endId;
            var component = pairEnd == start.Id || !shape.HasUnboundedQuantifier && shape.Bound == 0
                ? new List<ulong> { start.Id }
                : await ConnectedEndpointsAsync(operation, start.Id, connectivity.Direction, connectivity.Types, budget, componentMemory, token).ConfigureAwait(false);
            foreach (ulong target in component)
            {
                token.ThrowIfCancellationRequested();
                operation.EnsureActive();
                if (pairEnd is { } bound && target != bound) { continue; }
                if (operation.Updates.FindNode(target) is not { } storedTarget ||
                    shape.Nodes[^1].Any(node => !GraphLabelEvaluator.Accepts(node, storedTarget.Labels))) { continue; }
                budget.SelectorPairs++;
                long remaining = selection.Count ?? long.MaxValue;
                // Shortest searches deepen one hop at a time. Each round retains a DFS
                // history frontier, not all histories of the breadth-first search.
                // Revisited prefixes are charged again as physical statement work.
                int maximum = shape.HasUnboundedQuantifier ? 64 : (int)shape.Bound;
                bool any = selection.Selector.Kind == GqlPathSelectorKind.Any;
                int first = selection.OrderByLength || any ? 0 : maximum;
                for (int length = first; length <= maximum; length++)
                {
                    using var outputMemory = new GraphMemoryOwner(budget.Memory);
                    outputMemory.Add(640); // One retained truncation prefix, at most 65 identities.
                    var matches = new List<PathMatch>();
                    var search = new PathSearch { Start = start.Id, End = target, HopLimit = length, ExactLength = selection.OrderByLength };
                    await foreach (var match in SearchPathStreamAsync(database, operation, searchPlan, input, budget,
                        parameters, token, search: search).ConfigureAwait(false))
                    {
                        budget.Consume();
                        budget.SelectorCandidates++;
                        // Reserve the row, path, entity payloads and sorting storage before
                        // retaining output beyond the matcher's borrowed-current lifetime.
                        outputMemory.Add(96);
                        outputMemory.RetainValue(match.Bindings, token);
                        outputMemory.RetainValue(match.Path, token);
                        // Completed checks belong to the matcher's borrowed state, not
                        // the retained sorting buffer. The buffer needs only row/path data.
                        matches.Add(match with { Properties = null, Discovery = matches.Count });
                    }
                    if (!selection.OrderByLength && !any && search.Truncated)
                    { throw new GraphPlanningException("CG05004", ErrorMessages.PathHopLimitExceeded); }
                    try
                    {
                        matches.Sort((left, right) =>
                        {
                            token.ThrowIfCancellationRequested();
                            operation.EnsureActive();
                            budget.Consume();
                            int compared = ComparePathIdentity(left.Path!, right.Path!);
                            return compared != 0 ? compared : left.Discovery.CompareTo(right.Discovery);
                        });
                    }
                    catch (InvalidOperationException error) when (error.InnerException is { } cause)
                    {
                        // List.Sort wraps comparer failures. Preserve cancellation and coded
                        // work/lifecycle refusals instead of exposing its uncoded wrapper.
                        ExceptionDispatchInfo.Capture(cause).Throw();
                        throw;
                    }
                    // ANY is identity-ranked, not length-ranked. A candidate is final
                    // only when no truncated prefix could precede it lexicographically.
                    if (any && search.Truncated && (matches.Count == 0 ||
                        ComparePrefix(matches[0].Path!, search.FirstTruncatedPrefix!) > 0))
                    {
                        if (length == maximum) { throw new GraphPlanningException("CG05004", ErrorMessages.PathHopLimitExceeded); }
                        continue;
                    }
                    GraphPath? previous = null;
                    bool hasGroup = false;
                    foreach (var match in matches)
                    {
                        token.ThrowIfCancellationRequested();
                        operation.EnsureActive();
                        if (previous is not null && ComparePathIdentity(previous, match.Path!) == 0) { continue; }
                        previous = match.Path;
                        if (remaining == 0) { break; }
                        hasGroup = true;
                        budget.Consume();
                        budget.SelectorSelected++;
                        yield return match; // All search properties have already been checked.
                        if (!selection.CountLengthGroups && selection.Count is not null) { remaining--; }
                    }
                    if (selection.CountLengthGroups && hasGroup) { remaining--; }
                    if (remaining == 0) { break; }
                    if (length == maximum && search.Truncated)
                    { throw new GraphPlanningException("CG05004", ErrorMessages.PathHopLimitExceeded); }
                    if (!search.Truncated || !selection.OrderByLength && !any) { break; }
                }
            }
        }
    }

    private static (GraphIncidentDirection Direction, IReadOnlyCollection<Guid>? Types) Connectivity(
        GraphPathShape shape, GraphOperation operation, ExpansionBudget budget, GraphMemoryOwner memory, CancellationToken token)
    {
        bool outgoing = false, incoming = false;
        HashSet<Guid>? types = [];
        using var pendingMemory = new GraphMemoryOwner(budget.Memory);
        pendingMemory.Add(256);
        var pending = new Stack<GraphPathShape>();
        pendingMemory.Add(16);
        pending.Push(shape);
        while (pending.TryPop(out var body))
        {
            token.ThrowIfCancellationRequested();
            if (body.Edge is { } edge)
            {
                outgoing |= edge.Direction != GqlPatternDirection.Incoming;
                incoming |= edge.Direction != GqlPatternDirection.Outgoing;
                var selected = operation.Updates.SelectTypes(edge);
                memory.Add(64 + 16L * (selected?.Length ?? 0)); // Retained pattern/type selection and its cache entry.
                if (selected is null) { types = null; }
                else if (types is not null)
                {
                    foreach (var type in selected)
                    {
                        if (types.Contains(type)) { continue; }
                        budget.Consume();
                        memory.Add(96);
                        types.Add(type);
                    }
                }
            }
            foreach (var segment in body.Segments) { pendingMemory.Add(16); pending.Push(segment.Body); }
        }
        return (outgoing && incoming ? GraphIncidentDirection.Both : incoming ? GraphIncidentDirection.Incoming : GraphIncidentDirection.Outgoing, types);
    }

    private static async ValueTask<List<ulong>> ConnectedEndpointsAsync(GraphOperation operation, ulong start,
        GraphIncidentDirection direction, IReadOnlyCollection<Guid>? types, ExpansionBudget budget, GraphMemoryOwner memory, CancellationToken token)
    {
        var found = new HashSet<ulong>();
        var pending = new Queue<ulong>();
        var ordered = new List<ulong>();
        Add(start);
        while (pending.TryDequeue(out ulong current))
        {
            token.ThrowIfCancellationRequested();
            operation.EnsureActive();
            await foreach (var edge in operation.Updates.StreamIncidentAsync(current, direction,
                types, token, budget).ConfigureAwait(false))
            { Add(edge.SourceId == current ? edge.TargetId : edge.SourceId); }
        }
        ordered.Sort();
        return ordered;

        void Add(ulong identity)
        {
            if (found.Contains(identity)) { return; }
            budget.Consume();
            memory.Add(192); // Set growth, queue capacity and ordered endpoint storage.
            found.Add(identity);
            pending.Enqueue(identity);
            ordered.Add(identity);
        }
    }

    private static int CompareExtendedPrefix(List<GraphRelationship> path, ulong edge, ulong[] prefix)
    {
        for (int i = 0; i < path.Count; i++)
        {
            int compared = path[i].Id.Value.CompareTo(prefix[i]);
            if (compared != 0) { return compared; }
        }
        return edge.CompareTo(prefix[^1]);
    }

    private static int ComparePrefix(GraphPath path, ulong[] prefix)
    {
        for (int i = 0; i < Math.Min(path.Relationships.Count, prefix.Length); i++)
        {
            int compared = path.Relationships[i].Id.Value.CompareTo(prefix[i]);
            if (compared != 0) { return compared; }
        }
        return path.Relationships.Count.CompareTo(prefix.Length);
    }

    private static int ComparePathIdentity(GraphPath left, GraphPath right)
    {
        for (int i = 0; i < Math.Min(left.Relationships.Count, right.Relationships.Count); i++)
        {
            int compared = left.Relationships[i].Id.Value.CompareTo(right.Relationships[i].Id.Value);
            if (compared != 0) { return compared; }
        }
        int length = left.Relationships.Count.CompareTo(right.Relationships.Count);
        if (length != 0) { return length; }
        for (int i = 0; i < left.Nodes.Count; i++)
        {
            int compared = left.Nodes[i].Id.Value.CompareTo(right.Nodes[i].Id.Value);
            if (compared != 0) { return compared; }
        }
        return 0;
    }

    private static MatchedPredicate? AddPredicates(IReadOnlyList<GraphPathPrefilter> filters, MatchedPredicate? previous)
    {
        foreach (var filter in filters) { previous = new MatchedPredicate(filter.Predicate, previous); }
        return previous;
    }

    private static IReadOnlyDictionary<string, object?> WithLocals(IReadOnlyDictionary<string, object?> bindings,
        IReadOnlyDictionary<string, object?>? locals)
    {
        if (locals is null) { return bindings; }
        var merged = new Dictionary<string, object?>(bindings, StringComparer.Ordinal);
        foreach (var pair in locals) { merged[pair.Key] = pair.Value; }
        return merged;
    }

    private static bool PredicateMatches(GqlExpression expression, IReadOnlyDictionary<string, object?> bindings,
        GraphOperation operation, IReadOnlyDictionary<string, object?>? parameters)
        => GraphExpressionEvaluator.Boolean(GraphExpressionEvaluator.Evaluate(expression, bindings,
            operation.EvaluationContext, parameters), expression) is true;

    private static bool CheckPredicates(MatchedPredicate? check, MatchedPredicate? stop,
        IReadOnlyDictionary<string, object?> bindings, GraphOperation operation, IReadOnlyDictionary<string, object?>? parameters)
    {
        for (; check != stop; check = check!.Previous)
        { if (!PredicateMatches(check!.Expression, WithLocals(bindings, check.Locals), operation, parameters)) { return false; } }
        return true;
    }

    private static bool CheckProperties(MatchedProperties? check, MatchedProperties? stop,
        IReadOnlyDictionary<string, object?> bindings, GraphOperation operation, IReadOnlyDictionary<string, object?>? parameters,
        bool deferUnbound = false)
    {
        for (; check != stop; check = check!.Previous)
        {
            if (!PropertiesMatch(check!.Expected, check.Actual, WithLocals(bindings, (check as ScopedProperties)?.Locals),
                operation.EvaluationContext, parameters, deferUnbound)) { return false; }
        }
        return true;
    }

    private static bool CheckIteration(ref PathState state, EndIterationTask end, GraphOperation operation,
        IReadOnlyDictionary<string, object?>? parameters)
    {
        var stop = (end as FilteredIterationTask)?.Predicates;
        var predicates = stop;
        var properties = end.Properties;
        Dictionary<string, object?>? locals = null;
        foreach (string name in end.Repeat.Names)
        { (locals ??= new(StringComparer.Ordinal))[name] = state.Bindings[name]; }
        for (var check = Predicates(state); check != stop; check = check!.Previous)
        {
            var bindings = WithLocals(state.Bindings, check!.Locals);
            if (GraphValueExpressions.ReferencesUnbound(check.Expression, bindings))
            { predicates = check with { Previous = predicates, Locals = locals }; }
            else if (!PredicateMatches(check.Expression, bindings, operation, parameters)) { return false; }
        }
        for (var check = state.Properties; check != end.Properties; check = check!.Previous)
        {
            var bindings = WithLocals(state.Bindings, (check as ScopedProperties)?.Locals);
            if (check!.Expected.Values.Any(value => GraphValueExpressions.ReferencesUnbound(value, bindings)))
            { properties = new ScopedProperties(check, properties, locals); }
            else if (!PropertiesMatch(check.Expected, check.Actual, bindings, operation.EvaluationContext, parameters)) { return false; }
        }
        state = WithPredicates(state with { Properties = properties }, predicates);
        return true;
    }
}

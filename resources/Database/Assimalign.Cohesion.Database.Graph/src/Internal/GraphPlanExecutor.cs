using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Assimalign.Cohesion.Database.Execution;
using Assimalign.Cohesion.Database.Graph.Language;
using Assimalign.Cohesion.Database.Graph.Storage;
using Assimalign.Cohesion.Database.Language;
using Assimalign.Cohesion.Database.Types;

namespace Assimalign.Cohesion.Database.Graph.Internal;

internal static class GraphPlanExecutor
{
    // Finite syntax guarantees termination; this cap also bounds materialization.
    private const int MaximumMatches = 100_000;
    internal static async ValueTask<QueryResult> ExecuteAsync(GraphDatabaseInstance database, GraphOperation operation,
        GqlQueryStatement statement, IReadOnlyDictionary<string, object?>? parameters, CancellationToken token, bool paths = false)
    {
        operation.EnsureActive();
        var error = statement.Diagnostics.FirstOrDefault(item => item.Severity == DiagnosticSeverity.Error);
        if (error is not null) { throw new DatabaseParseException($"GQL parse error {error.Code}: {error.Message}"); }
        if (paths)
        {
            var query = statement.GqlExpression;
            if (query.CatalogSurface is not null || query.Matches.Count == 0 || query.Creates.Count != 0 ||
                query.DeleteVariables.Count != 0 || query.DetachDelete || query.Projections.Count != 1 || query.Projections[0].Property is not null)
            { throw new DatabaseException("COHDBG001: Path execution requires a read-only MATCH with exactly one path or bound entity projection."); }
        }
        if (statement.GqlExpression.CatalogSurface is { } surface)
        {
            return GraphCatalogIntrospection.Execute(database, operation, statement.GqlExpression, surface, token);
        }
        var plan = new GraphPlanner(database, operation.Context.Snapshot).Plan(statement.GqlExpression);
        if (plan.Query.Creates.Count > 0 || plan.Query.DeleteVariables.Count > 0)
        { await database.LockWriterAsync(operation.Context, token).ConfigureAwait(false); }
        var bindings = new List<Dictionary<string, object>> { new(StringComparer.Ordinal) };
        var budget = new ExpansionBudget();
        foreach (var path in plan.Matches)
        {
            var matched = new List<Dictionary<string, object>>();
            foreach (var binding in bindings)
            { await MatchAsync(database, operation, path, binding, matched, budget, token).ConfigureAwait(false); }
            bindings = matched;
        }
        bindings = bindings.Where(binding => plan.Query.Predicate is null || GraphExpressionEvaluator.Evaluate(plan.Query.Predicate, binding) is true).ToList();
        if (paths)
        {
            string variable = plan.Query.Projections[0].Variable;
            var results = new List<GraphPath>(bindings.Count);
            foreach (var binding in bindings)
            {
                token.ThrowIfCancellationRequested();
                results.Add(binding[variable] switch
                {
                    GraphPath path => path,
                    GraphNode node => new GraphPath([node], []),
                    GraphRelationship relationship => ProjectRelationship(database, operation, relationship),
                    _ => throw new DatabaseException("COHDBG001: Path execution requires a path or bound entity projection."),
                });
            }
            return new GraphPathsQueryResult(results.AsReadOnly());
        }
        long affected = 0;
        foreach (var binding in bindings)
        {
            foreach (var path in plan.Query.Creates)
            { affected += await CreateAsync(database, operation, path, binding, token).ConfigureAwait(false); }
        }
        // Delete relationships first, so explicit DELETE r,a can remove a connected node.
        var relationships = new HashSet<GraphRelationshipId>();
        var nodes = new HashSet<GraphNodeId>();
        foreach (var binding in bindings)
        {
            foreach (string variable in plan.Query.DeleteVariables)
            {
                if (binding[variable] is GraphNode node) { nodes.Add(node.Id); }
                else if (binding[variable] is GraphRelationship relationship) { relationships.Add(relationship.Id); }
            }
        }
        foreach (var id in relationships)
        {
            if (database.Store.FindRelationship(id.Value, operation.Context.Snapshot) is not null) { affected++; }
            await database.Store.DeleteRelationshipAsync(id.Value, operation.Context, token).ConfigureAwait(false);
        }
        foreach (var id in nodes)
        {
            if (database.Store.FindNode(id.Value, operation.Context.Snapshot) is not null) { affected++; }
            await database.Store.DeleteNodeAsync(id.Value, plan.Query.DetachDelete, operation.Context, token).ConfigureAwait(false);
        }
        if (plan.Query.Projections.Count == 0) { return new GraphMutationResult(affected); }
        var columns = plan.Query.Projections.Select((projection, i) => new QueryColumn
        { Name = projection.Alias ?? projection.Variable + (projection.Property is null ? "" : "." + projection.Property), Ordinal = i, Type = DatabaseType.Null }).ToArray();
        var rows = bindings.Select(binding => plan.Query.Projections.Select(projection => projection.Property is { } property
            ? GraphExpressionEvaluator.Property(binding[projection.Variable], property) : binding[projection.Variable]).ToArray()).ToArray();
        return new GraphQueryResult(columns, rows);
    }

    private static async ValueTask MatchAsync(GraphDatabaseInstance database, GraphOperation operation, GraphPathPlan plan,
        Dictionary<string, object> input, List<Dictionary<string, object>> output, ExpansionBudget budget, CancellationToken token)
    {
        var path = plan.Pattern;
        var anchor = plan.Anchor;
        IReadOnlyList<StoredGraphNode> candidates;
        if (path.Nodes[anchor.NodeIndex].Variable is { } variable && input.TryGetValue(variable, out var existing) && existing is GraphNode bound)
        {
            candidates = database.Store.FindNode(bound.Id.Value, operation.Context.Snapshot) is { } node ? [node] : [];
        }
        else if (anchor.Property is { } property)
        { candidates = await database.Store.SearchIndexAsync(anchor.Label!, property, anchor.Value!, operation.Context.Snapshot, token).ConfigureAwait(false); }
        else { candidates = database.Store.GetNodes(anchor.Label, operation.Context.Snapshot); }
        // Expand right of an arbitrary indexed anchor, then left, preserving source positions.
        var steps = Enumerable.Range(anchor.NodeIndex, path.Relationships.Count - anchor.NodeIndex)
            .Select(index => (Edge: index, From: index, To: index + 1))
            .Concat(Enumerable.Range(0, anchor.NodeIndex).Reverse().Select(index => (Edge: index, From: index + 1, To: index))).ToArray();
        foreach (var candidate in candidates)
        {
            token.ThrowIfCancellationRequested();
            budget.Consume();
            var bindings = new Dictionary<string, object>(input, StringComparer.Ordinal);
            var node = GraphDatabaseInstance.Materialize(candidate);
            if (!AcceptNode(path.Nodes[anchor.NodeIndex], node, bindings)) { continue; }
            var positions = new GraphNode[path.Nodes.Count];
            positions[anchor.NodeIndex] = node;
            await Expand(0, positions, new GraphRelationship[path.Relationships.Count], bindings, []).ConfigureAwait(false);
        }
        async ValueTask Expand(int step, GraphNode[] positions, GraphRelationship[] relationships,
            Dictionary<string, object> bindings, HashSet<ulong> used)
        {
            token.ThrowIfCancellationRequested();
            operation.EnsureActive();
            if (step == steps.Length)
            {
                if (output.Count >= MaximumMatches) { throw new DatabaseException("COHDBG004: The query exceeded 100000 path matches."); }
                if (path.Variable is { } pathVariable)
                { bindings.Add(pathVariable, new GraphPath(Array.AsReadOnly(positions), Array.AsReadOnly(relationships))); }
                output.Add(bindings);
                return;
            }
            var current = steps[step];
            ulong from = positions[current.From].Id.Value;
            var pattern = path.Relationships[current.Edge];
            foreach (var edge in await database.Store.GetIncidentAsync(from, operation.Context.Snapshot, token).ConfigureAwait(false))
            {
                budget.Consume();
                if (used.Contains(edge.Id) || pattern.Type is { } type && edge.Type != type) { continue; }
                // A leftward expansion reverses the pattern direction, not the stored edge.
                bool forward = current.To > current.From;
                bool outgoing = pattern.Direction == GqlPatternDirection.Outgoing && forward || pattern.Direction == GqlPatternDirection.Incoming && !forward;
                bool incoming = pattern.Direction == GqlPatternDirection.Incoming && forward || pattern.Direction == GqlPatternDirection.Outgoing && !forward;
                if (outgoing && edge.SourceId != from || incoming && edge.TargetId != from) { continue; }
                ulong next = edge.SourceId == from ? edge.TargetId : edge.SourceId;
                if (database.Store.FindNode(next, operation.Context.Snapshot) is not { } target) { continue; }
                var copy = new Dictionary<string, object>(bindings, StringComparer.Ordinal);
                var relationship = GraphDatabaseInstance.Materialize(edge);
                if (!Accept(pattern.Variable, relationship, copy) || !PropertiesMatch(pattern.Properties, relationship.Properties)) { continue; }
                var node = GraphDatabaseInstance.Materialize(target);
                if (!AcceptNode(path.Nodes[current.To], node, copy)) { continue; }
                var nextPositions = (GraphNode[])positions.Clone();
                nextPositions[current.To] = node;
                var nextRelationships = (GraphRelationship[])relationships.Clone();
                nextRelationships[current.Edge] = relationship;
                var nextUsed = new HashSet<ulong>(used) { edge.Id };
                await Expand(step + 1, nextPositions, nextRelationships, copy, nextUsed).ConfigureAwait(false);
            }
        }
    }

    private static GraphPath ProjectRelationship(GraphDatabaseInstance database, GraphOperation operation, GraphRelationship relationship)
    {
        // Resolve the bound relationship's endpoints in the same statement snapshot, including anonymous nodes.
        // This projects an engine entity binding directly; scalar projection rows are never involved.
        var snapshot = operation.Context.Snapshot;
        if (database.Store.FindNode(relationship.From.Value, snapshot) is not { } from ||
            database.Store.FindNode(relationship.To.Value, snapshot) is not { } to)
        { throw new DatabaseException("COHDBG003: A matched relationship has a missing endpoint."); }
        return new GraphPath([GraphDatabaseInstance.Materialize(from), GraphDatabaseInstance.Materialize(to)], [relationship]);
    }

    private static bool AcceptNode(GqlNodePattern pattern, GraphNode node, Dictionary<string, object> bindings)
        => pattern.Labels.All(node.Labels.Contains) && PropertiesMatch(pattern.Properties, node.Properties) && Accept(pattern.Variable, node, bindings);
    private static bool Accept(string? variable, object entity, Dictionary<string, object> bindings)
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
    private static bool PropertiesMatch(IReadOnlyDictionary<string, object?> expected, IReadOnlyDictionary<string, object?> actual)
        => expected.All(item => actual.TryGetValue(item.Key, out var value) && GraphExpressionEvaluator.Equal(item.Value, value));

    private static async ValueTask<long> CreateAsync(GraphDatabaseInstance database, GraphOperation operation, GqlPathPattern path,
        Dictionary<string, object> bindings, CancellationToken token)
    {
        long affected = 0;
        var nodes = new GraphNode[path.Nodes.Count];
        for (int i = 0; i < nodes.Length; i++)
        {
            var pattern = path.Nodes[i];
            if (pattern.Variable is { } variable && bindings.TryGetValue(variable, out var bound))
            {
                nodes[i] = (GraphNode)bound;
                if (!AcceptNode(pattern, nodes[i], bindings)) { throw new DatabaseException("COHDBG003: An inserted endpoint conflicts with its existing binding."); }
            }
            else
            {
                nodes[i] = await database.CreateNodeCoreAsync(operation, pattern.Labels, pattern.Properties, token).ConfigureAwait(false);
                Accept(pattern.Variable, nodes[i], bindings);
                affected++;
            }
        }
        for (int i = 0; i < path.Relationships.Count; i++)
        {
            var pattern = path.Relationships[i];
            if (pattern.Variable is { } variable && bindings.ContainsKey(variable))
            { throw new DatabaseException("COHDBG001: An inserted relationship variable must be new."); }
            bool outgoing = pattern.Direction == GqlPatternDirection.Outgoing;
            var relationship = await database.CreateRelationshipCoreAsync(operation, nodes[outgoing ? i : i + 1].Id,
                nodes[outgoing ? i + 1 : i].Id, pattern.Type!, pattern.Properties, token).ConfigureAwait(false);
            Accept(pattern.Variable, relationship, bindings);
            affected++;
        }
        return affected;
    }

    private sealed class ExpansionBudget
    {
        private int _remaining = 1_000_000;
        internal void Consume()
        {
            if (--_remaining < 0) { throw new DatabaseException("COHDBG004: The query exceeded 1000000 candidate expansions."); }
        }
    }
}

internal sealed class GraphMutationResult(long count) : QueryResult
{
    public override QueryResultStatus Status => QueryResultStatus.Success;
    public override long AffectedCount => count;
    public override IReadOnlyList<Diagnostic>? Diagnostics => null;
}

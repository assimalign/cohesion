using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Assimalign.Cohesion.Database.Execution;
using Assimalign.Cohesion.Database.Execution.Internal;
using Assimalign.Cohesion.Database.Graph.Language;
using Assimalign.Cohesion.Database.Graph.Storage;
using Assimalign.Cohesion.Database.Language;
using Assimalign.Cohesion.Database.Types;

namespace Assimalign.Cohesion.Database.Graph.Internal;

internal static class GraphPlanExecutor
{
    // Finite syntax guarantees termination; this cap also bounds materialization.
    private const int MaximumMatches = 100_000;
    internal static async ValueTask<DatabaseResult> ExecuteAsync(GraphDatabase database, GraphOperation operation,
        GqlQueryStatement statement, IReadOnlyDictionary<string, object?>? parameters, CancellationToken token, bool paths = false, bool scalarsOnly = false)
    {
        operation.EnsureActive();
        var error = statement.Diagnostics.FirstOrDefault(item => item.Severity == DiagnosticSeverity.Error);
        if (error is not null)
        {
            if (error.Code == GraphStatementTooComplex.ParserCode) { throw GraphStatementTooComplex.FromParse(error); }
            throw new DatabaseParseException($"GQL parse error {error.Code}: {error.Message}", statement.Diagnostics);
        }
        if (paths)
        {
            var query = statement.GqlExpression;
            if (query.CatalogSurface is not null || query.MatchClauses is null || query.MatchClauses.Count == 0 || query.Creates.Count != 0 ||
                query.DeleteVariables.Count != 0 || query.DetachDelete)
            { throw new GraphPlanningException("COHDBG001", ErrorMessages.PathProjectionRequired, query.Location); }
        }
        if (statement.GqlExpression.CatalogSurface is { } surface)
        {
            // Catalog requests have no parameter operands, but the shared contract still rejects
            // ambiguous normalized keys, including unused ones, on every execution.
            ParameterBindings.Normalize(parameters, name => new GraphPlanningException("COHDBG014",
                string.Format(System.Globalization.CultureInfo.CurrentCulture, ErrorMessages.ParameterCollision, name), statement.GqlExpression.Location));
            return GraphCatalogIntrospection.Execute(database, operation, statement.GqlExpression, surface, token);
        }
        var plan = new GraphPlanner(database, operation.Context.Snapshot).Plan(statement.GqlExpression, parameters, paths);
        parameters = plan.Parameters;
        // Validate result shape before locking a writer, expanding a MATCH or applying mutations.
        if (scalarsOnly && plan.Projections.Any(projection => projection.Kind != GraphProjectionKind.Scalar))
        { throw new GraphPlanningException("COHDBG001", ErrorMessages.ExecuteScalarRequired, plan.Query.Location); }
        if (plan.Query.Creates.Count > 0 || plan.Query.DeleteVariables.Count > 0)
        { await database.LockWriterAsync(operation.Context, token).ConfigureAwait(false); }
        // An unsatisfiable mandatory clause empties the statement without reads (#1228).
        // An optional clause carries its own flag and null-extends the running rows below.
        var bindings = plan.MatchesNothing
            ? new List<Dictionary<string, object?>>()
            : new List<Dictionary<string, object?>> { new(StringComparer.Ordinal) };
        var budget = new ExpansionBudget();
        foreach (var clause in plan.MatchClauses)
        {
            var matched = new List<Dictionary<string, object?>>();
            foreach (var binding in bindings)
            {
                token.ThrowIfCancellationRequested();
                operation.EnsureActive();
                var candidates = new List<Dictionary<string, object?>>();
                if (!clause.MatchesNothing)
                {
                    candidates.Add(binding);
                    foreach (var path in clause.Paths)
                    {
                        var expanded = new List<Dictionary<string, object?>>();
                        foreach (var candidate in candidates)
                        { await MatchAsync(database, operation, path, candidate, expanded, budget, parameters, token).ConfigureAwait(false); }
                        candidates = expanded;
                    }
                }
                bool found = false;
                foreach (var candidate in candidates)
                {
                    token.ThrowIfCancellationRequested();
                    if (clause.Predicate is not null && GraphExpressionEvaluator.Boolean(
                        GraphExpressionEvaluator.Evaluate(clause.Predicate, candidate, parameters), clause.Predicate) is not true) { continue; }
                    AddBinding(matched, candidate);
                    found = true;
                }
                if (!found && clause.Optional)
                {
                    var extended = new Dictionary<string, object?>(binding, StringComparer.Ordinal);
                    foreach (string variable in clause.NewVariables) { extended.Add(variable, null); }
                    AddBinding(matched, extended);
                }
            }
            bindings = matched;
        }
        if (paths)
        {
            string variable = ((GqlVariableExpression)plan.Projections[0].Expression).Variable;
            var results = new List<GraphPath>(bindings.Count);
            var seen = plan.Distinct is null ? null : new HashSet<object?>(GraphValueComparer.Instance);
            foreach (var binding in bindings)
            {
                token.ThrowIfCancellationRequested();
                if (seen is not null && !seen.Add(binding[variable])) { continue; }
                results.Add(binding[variable] switch
                {
                    GraphPath path => path,
                    GraphNode node => new GraphPath([node], []),
                    GraphRelationship relationship => ProjectRelationship(database, operation, relationship),
                    _ => throw new GraphPlanningException("COHDBG001", "Path execution requires a path or bound entity projection.", plan.Query.Location),
                });
            }
            return new GraphPathsQueryResult(results.AsReadOnly(), plan.Warnings);
        }
        long affected = 0;
        foreach (var binding in bindings)
        {
            foreach (var path in plan.Query.Creates)
            { affected += await CreateAsync(database, operation, path, binding, parameters, token).ConfigureAwait(false); }
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
        if (plan.Projections.Count == 0) { return new GraphMutationResult(affected, plan.Warnings); }
        var columns = plan.Projections.Select((projection, i) => new DatabaseColumn
        { Name = projection.Name, Ordinal = i, Type = DatabaseType.Null }).ToArray();
        var rows = new List<object?[]>(bindings.Count);
        var distinct = plan.Distinct is null ? null : new HashSet<object?[]>(GraphRowComparer.Instance);
        foreach (var binding in bindings)
        {
            token.ThrowIfCancellationRequested();
            var row = plan.Projections.Select(projection => GraphExpressionEvaluator.Evaluate(projection.Expression, binding, parameters)).ToArray();
            if (distinct is null || distinct.Add(row)) { rows.Add(row); }
        }
        return new GraphQueryResult(columns, rows.ToArray(), plan.Warnings);
    }

    internal static async ValueTask MatchAsync(GraphDatabase database, GraphOperation operation, GraphPathPlan plan,
        Dictionary<string, object?> input, List<Dictionary<string, object?>> output, ExpansionBudget budget, IReadOnlyDictionary<string, object?>? parameters, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        operation.EnsureActive();
        var path = plan.Pattern;
        var anchor = plan.Anchor;
        // Explicit NULL is already bound. It cannot match an element and must never become
        // an unbound full-scan anchor, including at a non-anchor position in the same path.
        foreach (var node in path.Nodes)
        { if (node.Variable is { } name && input.TryGetValue(name, out var value) && value is null) { return; } }
        foreach (var edge in path.Relationships)
        { if (edge.Variable is { } name && input.TryGetValue(name, out var value) && value is null) { return; } }
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
            if (!AcceptNode(path.Nodes[anchor.NodeIndex], node, bindings, parameters)) { continue; }
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
                AddBinding(output, bindings);
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
                if (!Accept(pattern.Variable, relationship, copy) || !PropertiesMatch(pattern.Properties, relationship.Properties, parameters)) { continue; }
                var node = GraphDatabase.Materialize(target);
                if (!AcceptNode(path.Nodes[current.To], node, copy, parameters)) { continue; }
                var nextPositions = (GraphNode[])positions.Clone();
                nextPositions[current.To] = node;
                var nextRelationships = (GraphRelationship[])relationships.Clone();
                nextRelationships[current.Edge] = relationship;
                var nextUsed = new HashSet<ulong>(used) { edge.Id };
                await Expand(step + 1, nextPositions, nextRelationships, copy, nextUsed).ConfigureAwait(false);
            }
        }
    }

    private static void AddBinding(List<Dictionary<string, object?>> output, Dictionary<string, object?> binding)
    {
        if (output.Count >= MaximumMatches) { throw new GraphPlanningException("COHDBG004", ErrorMessages.MatchLimitExceeded); }
        output.Add(binding);
    }

    private static GraphPath ProjectRelationship(GraphDatabase database, GraphOperation operation, GraphRelationship relationship)
    {
        // Resolve the bound relationship's endpoints in the same statement snapshot, including anonymous nodes.
        // This projects an engine entity binding directly; scalar projection rows are never involved.
        var snapshot = operation.Context.Snapshot;
        if (database.Store.FindNode(relationship.From.Value, snapshot) is not { } from ||
            database.Store.FindNode(relationship.To.Value, snapshot) is not { } to)
        { throw new GraphPlanningException("COHDBG003", "A matched relationship has a missing endpoint."); }
        return new GraphPath([GraphDatabase.Materialize(from), GraphDatabase.Materialize(to)], [relationship]);
    }

    private static bool AcceptNode(GqlNodePattern pattern, GraphNode node, Dictionary<string, object?> bindings, IReadOnlyDictionary<string, object?>? parameters)
        => GraphLabelEvaluator.Accepts(pattern, node.Labels) && PropertiesMatch(pattern.Properties, node.Properties, parameters) && Accept(pattern.Variable, node, bindings);
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
    private static bool PropertiesMatch(IReadOnlyDictionary<string, GqlExpression> expected, IReadOnlyDictionary<string, object?> actual, IReadOnlyDictionary<string, object?>? parameters)
        => expected.All(item => actual.TryGetValue(item.Key, out var value) && GraphExpressionEvaluator.Equal(GraphExpressionEvaluator.Scalar(item.Value, parameters), value));

    private static async ValueTask<long> CreateAsync(GraphDatabase database, GraphOperation operation, GqlPathPattern path,
        Dictionary<string, object?> bindings, IReadOnlyDictionary<string, object?>? parameters, CancellationToken token)
    {
        long affected = 0;
        var nodes = new GraphNode[path.Nodes.Count];
        for (int i = 0; i < nodes.Length; i++)
        {
            var pattern = path.Nodes[i];
            if (pattern.Variable is { } variable && bindings.TryGetValue(variable, out var bound))
            {
                if (bound is not GraphNode node || !AcceptNode(pattern, node, bindings, parameters))
                { throw new GraphPlanningException("COHDBG003", ErrorMessages.InsertEndpointInvalid); }
                nodes[i] = node;
            }
            else
            {
                nodes[i] = await database.CreateNodeCoreAsync(operation, GraphLabelEvaluator.InsertLabels(pattern), EvaluateProperties(pattern.Properties, parameters), token).ConfigureAwait(false);
                Accept(pattern.Variable, nodes[i], bindings);
                affected++;
            }
        }
        for (int i = 0; i < path.Relationships.Count; i++)
        {
            var pattern = path.Relationships[i];
            if (pattern.Variable is { } variable && bindings.ContainsKey(variable))
            { throw new GraphPlanningException("COHDBG001", "An inserted relationship variable must be new."); }
            // The planner admits only Outgoing and Incoming here.
            bool outgoing = pattern.Direction == GqlPatternDirection.Outgoing;
            var relationship = await database.CreateRelationshipCoreAsync(operation, nodes[outgoing ? i : i + 1].Id,
                nodes[outgoing ? i + 1 : i].Id, GraphLabelEvaluator.InsertType(pattern), EvaluateProperties(pattern.Properties, parameters), token).ConfigureAwait(false);
            Accept(pattern.Variable, relationship, bindings);
            affected++;
        }
        return affected;
    }

    private static IReadOnlyDictionary<string, object?> EvaluateProperties(
        IReadOnlyDictionary<string, GqlExpression> properties, IReadOnlyDictionary<string, object?>? parameters)
        => properties.ToDictionary(item => item.Key, item => GraphExpressionEvaluator.Scalar(item.Value, parameters), StringComparer.Ordinal);

    internal sealed class ExpansionBudget
    {
        private int _remaining = 1_000_000;
        internal int Consumed => 1_000_000 - _remaining;
        internal void Consume()
        {
            if (--_remaining < 0) { throw new GraphPlanningException("COHDBG004", "The query exceeded 1000000 candidate expansions."); }
        }
    }
}

internal sealed class GraphMutationResult : DatabaseResult
{
    private readonly long _count;
    private readonly IReadOnlyList<Diagnostic> _warnings;

    /// <summary>Initializes a new instance of the <see cref="GraphMutationResult"/> class.</summary>
    /// <param name="count">The number of graph entities the mutation affected.</param>
    /// <param name="warnings">The statement's warnings; a statement without a projection reports them here.</param>
    public GraphMutationResult(long count, IReadOnlyList<Diagnostic> warnings)
    {
        _count = count;
        _warnings = warnings;
    }

    public override DatabaseResultStatus Status => DatabaseResultStatus.Success;
    public override long AffectedCount => _count;
    public override IReadOnlyList<Diagnostic>? Diagnostics => _warnings.Count == 0 ? null : _warnings;
}

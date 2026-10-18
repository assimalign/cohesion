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
        GqlQueryStatement statement, IReadOnlyDictionary<string, object?>? parameters, CancellationToken token, bool paths = false, bool scalarsOnly = false, bool compositeValues = true)
    {
        operation.EnsureActive();
        var error = statement.Diagnostics.FirstOrDefault(item => item.Severity == DiagnosticSeverity.Error);
        if (error is not null)
        {
            if (error.Code == GraphStatementTooComplex.ParserCode) { throw GraphStatementTooComplex.FromParse(error); }
            throw new DatabaseParseException($"GQL parse error {error.Code}: {error.Message}", statement.Diagnostics);
        }
        if (paths && statement.GqlExpression.CatalogSurface is not null)
        { throw new GraphPlanningException("COHDBG001", ErrorMessages.PathProjectionRequired, statement.GqlExpression.Location); }
        if (statement.GqlExpression.CatalogSurface is { } surface)
        {
            // Catalog requests have no parameter operands, but the shared contract still rejects
            // ambiguous normalized keys, including unused ones, on every execution.
            ParameterBindings.Normalize(parameters, name => new GraphPlanningException("COHDBG014",
                string.Format(System.Globalization.CultureInfo.CurrentCulture, ErrorMessages.ParameterCollision, name), statement.GqlExpression.Location));
            return GraphCatalogIntrospection.Execute(database, operation, statement.GqlExpression, surface, token);
        }
        var plan = new GraphPlanner(database, operation.Context.Snapshot).Plan(statement.GqlExpression, parameters, paths, token);
        parameters = plan.Parameters;
        // Validate result shape before locking a writer, expanding a MATCH or applying mutations.
        if (scalarsOnly && plan.Projections.Any(projection => projection.Kind != GraphProjectionKind.Scalar))
        { throw new GraphPlanningException("COHDBG001", ErrorMessages.ExecuteScalarRequired, plan.Query.Location); }
        var compositeColumns = plan.Projections.Select(projection => projection.MayBeComposite).ToArray();
        long headerLength = 4;
        foreach (var projection in plan.Projections) { headerLength += 5L + System.Text.Encoding.UTF8.GetByteCount(projection.Name); }
        if (headerLength > GraphValueCodec.MaxPayloadLength)
        { throw new GraphPlanningException("COHDBG013", ErrorMessages.CompositeLimit, plan.Query.Location); }
        if (!compositeValues && compositeColumns.Any(value => value))
        { throw new GraphPlanningException("COHDBG016", ErrorMessages.CompositeCapabilityRequired, plan.Query.Location); }
        if (plan.Writes) { await database.LockWriterAsync(operation.Context, token).ConfigureAwait(false); }
        var bindings = new List<Dictionary<string, object?>> { new(StringComparer.Ordinal) };
        var budget = new ExpansionBudget();
        long affected = 0;
        for (int stageIndex = 0; stageIndex < plan.Stages.Count; stageIndex++)
        {
            token.ThrowIfCancellationRequested();
            operation.EnsureActive();
            var stage = plan.Stages[stageIndex].Stage;
            var stagePlan = plan.Stages[stageIndex].Plan;
            switch (stage)
            {
                case GqlMatchStage:
                    foreach (var clause in stagePlan.MatchClauses)
                    { bindings = await MatchClauseAsync(database, operation, clause, bindings, budget, parameters, token).ConfigureAwait(false); }
                    break;
                case GqlLetStage let:
                    foreach (var binding in bindings)
                    {
                        token.ThrowIfCancellationRequested();
                        foreach (var assignment in let.Bindings)
                        { binding.Add(assignment.Alias!, GraphExpressionEvaluator.Evaluate(assignment.Expression, binding, parameters)); }
                    }
                    break;
                case GqlFilterStage filter:
                    bindings = bindings.Where(binding =>
                    {
                        token.ThrowIfCancellationRequested();
                        return GraphExpressionEvaluator.Boolean(GraphExpressionEvaluator.Evaluate(filter.Predicate, binding, parameters), filter.Predicate) is true;
                    }).ToList();
                    break;
                case GqlForStage loop:
                    var expandedRows = new List<Dictionary<string, object?>>();
                    foreach (var binding in bindings)
                    {
                        token.ThrowIfCancellationRequested();
                        object? value = GraphExpressionEvaluator.Evaluate(loop.List, binding, parameters);
                        if (value is null) { continue; }
                        if (value is not System.Collections.IList list || value is byte[])
                        { throw new GraphPlanningException("COHDBG013", ErrorMessages.ForListRequired, loop.List.Location); }
                        foreach (object? item in list)
                        {
                            token.ThrowIfCancellationRequested();
                            var row = new Dictionary<string, object?>(binding, StringComparer.Ordinal) { [loop.Variable] = item };
                            AddBinding(expandedRows, row);
                        }
                    }
                    bindings = expandedRows;
                    break;
                case GqlInsertStage insertion:
                    foreach (var binding in bindings)
                    {
                        token.ThrowIfCancellationRequested();
                        foreach (var path in insertion.Paths)
                        { affected += await CreateAsync(database, operation, path, binding, parameters, token).ConfigureAwait(false); }
                    }
                    break;
                case GqlDeleteStage deletion:
                {
                    // Delete relationships first, so explicit DELETE r,a can remove a connected node.
                    var relationships = new HashSet<GraphRelationshipId>();
                    var nodes = new HashSet<GraphNodeId>();
                    foreach (var binding in bindings)
                    {
                        foreach (string variable in stagePlan.Query.DeleteVariables)
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
                        await database.Store.DeleteNodeAsync(id.Value, stagePlan.Query.DetachDelete, operation.Context, token).ConfigureAwait(false);
                    }
                    foreach (var binding in bindings) { foreach (string variable in deletion.Variables) { binding.Remove(variable); } }
                    break;
                }
                case GqlReturnStage projection when stageIndex != plan.Stages.Count - 1:
                    bindings = ProjectBindings(stagePlan, projection, bindings, parameters, token);
                    break;
                case GqlNextStage when plan.Stages[stageIndex - 1].Stage is not GqlReturnStage:
                    bindings = [new(StringComparer.Ordinal)];
                    break;
            }
        }
        if (plan.Projections.Count == 0) { return new GraphMutationResult(affected, plan.Warnings); }
        if (plan.Grouping is { } grouping) { bindings = GraphGrouping.Apply(grouping, bindings, parameters, token); }
        var columns = plan.Projections.Select((projection, i) => new DatabaseColumn
        { Name = projection.Name, Ordinal = i, Type = projection.Type }).ToArray();
        var rows = new List<GraphOrderedRow>(bindings.Count);
        var distinct = plan.Distinct is null ? null : new HashSet<object?[]>(GraphRowComparer.Instance);
        bool valueColumnsAdded = false;
        foreach (var binding in bindings)
        {
            token.ThrowIfCancellationRequested();
            var row = plan.Projections.Select(projection => GraphExpressionEvaluator.Evaluate(projection.Expression, binding, parameters)).ToArray();
            if (scalarsOnly)
            {
                for (int i = 0; i < row.Length; i++)
                {
                    bool unsigned = row[i] is byte or ushort or uint or ulong;
                    if (unsigned && !compositeValues)
                    { throw new GraphPlanningException("COHDBG016", ErrorMessages.CompositeCapabilityRequired, plan.Query.Location); }
                    if (unsigned || compositeValues && row[i] is decimal)
                    {
                        valueColumnsAdded |= !compositeColumns[i];
                        compositeColumns[i] = true;
                    }
                }
            }
            if (!paths) { GraphResultRowEncoder.Validate(row, compositeColumns); }
            if (distinct is null || distinct.Add(row))
            { rows.Add(new GraphOrderedRow(row, GraphSortPage.Keys(plan, binding, row, token), rows.Count)); }
        }
        // Unsigned and Decimal values are learned from rows. Recheck earlier rows when
        // a header flag changes their framing; untagged results never need this pass.
        if (valueColumnsAdded) { foreach (var row in rows) { GraphResultRowEncoder.Validate(row.Values, compositeColumns); } }
        var paged = GraphSortPage.Apply(rows, plan.SortPage, token);
        if (paths)
        {
            var results = paged.Select(row =>
            {
                token.ThrowIfCancellationRequested();
                return row[0] switch
                {
                    GraphPath path => path,
                    GraphNode node => new GraphPath([node], []),
                    GraphRelationship relationship => ProjectRelationship(database, operation, relationship),
                    _ => throw new GraphPlanningException("COHDBG001", ErrorMessages.PathProjectionRequired, plan.Query.Location),
                };
            }).ToArray();
            return new GraphPathsQueryResult(results, plan.Warnings);
        }
        return new GraphQueryResult(columns, paged, plan.Warnings, compositeColumns);
    }

    private static async ValueTask<List<Dictionary<string, object?>>> MatchClauseAsync(GraphDatabase database, GraphOperation operation,
        GraphMatchClausePlan clause, List<Dictionary<string, object?>> bindings, ExpansionBudget budget,
        IReadOnlyDictionary<string, object?>? parameters, CancellationToken token)
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
        return matched;
    }

    private static List<Dictionary<string, object?>> ProjectBindings(GraphPlan plan, GqlReturnStage stage,
        List<Dictionary<string, object?>> bindings, IReadOnlyDictionary<string, object?>? parameters, CancellationToken token)
    {
        if (plan.Grouping is { } grouping) { bindings = GraphGrouping.Apply(grouping, bindings, parameters, token); }
        var rows = new List<GraphOrderedRow>(bindings.Count);
        var scopes = new Dictionary<object?[], Dictionary<string, object?>>();
        var distinct = plan.Distinct is null ? null : new HashSet<object?[]>(GraphRowComparer.Instance);
        foreach (var binding in bindings)
        {
            token.ThrowIfCancellationRequested();
            var values = plan.Projections.Select(projection => GraphExpressionEvaluator.Evaluate(projection.Expression, binding, parameters)).ToArray();
            if (distinct is not null && !distinct.Add(values)) { continue; }
            var scope = plan.Grouping is null && plan.Distinct is null
                ? new Dictionary<string, object?>(binding, StringComparer.Ordinal) : new Dictionary<string, object?>(StringComparer.Ordinal);
            for (int i = 0; i < values.Length; i++)
            { if (plan.Projections[i].BindingName is { } name) { scope[name] = values[i]; } }
            scopes.Add(values, scope);
            rows.Add(new GraphOrderedRow(values, GraphSortPage.Keys(plan, binding, values, token), rows.Count));
        }
        var paged = GraphSortPage.Apply(rows, plan.SortPage, token);
        var result = new List<Dictionary<string, object?>>(paged.Length);
        foreach (var values in paged)
        {
            token.ThrowIfCancellationRequested();
            if (stage.Predicate is { } predicate && GraphExpressionEvaluator.Boolean(
                GraphExpressionEvaluator.Evaluate(predicate, scopes[values], parameters), predicate) is not true) { continue; }
            // Grouped bindings store expression keys. Every boundary re-keys the carried
            // output by variable name so later stages see an ordinary binding table.
            var carried = new Dictionary<string, object?>(StringComparer.Ordinal);
            for (int i = 0; i < values.Length; i++)
            {
                if (plan.Projections[i].BindingName is not { } name) { continue; }
                if (!carried.TryAdd(name, values[i]))
                { throw new GraphPlanningException("COHDBG015", string.Format(System.Globalization.CultureInfo.CurrentCulture,
                    ErrorMessages.DuplicateResultColumn, name), plan.Projections[i].Expression.Location ?? plan.Query.Location); }
            }
            AddBinding(result, carried);
        }
        return result;
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
                { if (!PropertiesMatch(path.Nodes[i].Properties, positions[i].Properties, bindings, parameters)) { return; } }
                for (int i = 0; i < relationships.Length; i++)
                { if (!PropertiesMatch(path.Relationships[i].Properties, relationships[i].Properties, bindings, parameters)) { return; } }
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
            actual.TryGetValue(item.Key, out var value) && GraphExpressionEvaluator.Equal(GraphExpressionEvaluator.PropertyScalar(item.Value, bindings, parameters), value));

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
                nodes[i] = await database.CreateNodeCoreAsync(operation, GraphLabelEvaluator.InsertLabels(pattern), EvaluateProperties(pattern.Properties, bindings, parameters), token).ConfigureAwait(false);
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
                nodes[outgoing ? i + 1 : i].Id, GraphLabelEvaluator.InsertType(pattern), EvaluateProperties(pattern.Properties, bindings, parameters), token).ConfigureAwait(false);
            Accept(pattern.Variable, relationship, bindings);
            affected++;
        }
        return affected;
    }

    private static IReadOnlyDictionary<string, object?> EvaluateProperties(
        IReadOnlyDictionary<string, GqlExpression> properties, IReadOnlyDictionary<string, object?> bindings, IReadOnlyDictionary<string, object?>? parameters)
        => properties.ToDictionary(item => item.Key, item => GraphExpressionEvaluator.PropertyScalar(item.Value, bindings, parameters), StringComparer.Ordinal);

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

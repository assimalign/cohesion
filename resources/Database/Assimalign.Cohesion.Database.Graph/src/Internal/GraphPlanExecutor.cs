using System;
using System.Collections.Generic;
using System.Globalization;
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

internal static partial class GraphPlanExecutor
{
    // Trail uniqueness and the hop cap bound expansion; this cap bounds materialization.
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
        var compositeColumns = plan.Projections.Select(projection => projection.MayBeComposite).ToArray();
        long headerLength = 4;
        foreach (var projection in plan.Projections) { headerLength += 5L + System.Text.Encoding.UTF8.GetByteCount(projection.Name); }
        if (headerLength > GraphValueCodec.MaxPayloadLength)
        { throw new GraphPlanningException("COHDBG013", ErrorMessages.CompositeLimit, plan.Query.Location); }
        if (!compositeValues && compositeColumns.Any(value => value))
        { throw new GraphPlanningException("COHDBG016", ErrorMessages.CompositeCapabilityRequired, plan.Query.Location); }
        if (scalarsOnly && plan.Projections.Any(projection => projection.Kind == GraphProjectionKind.EntityValue && projection.MayBeComposite))
        { throw new GraphPlanningException("COHDBG001", ErrorMessages.EntityListWireUnsupported, plan.Query.Location); }
        if (scalarsOnly && plan.Projections.Any(projection => projection.Kind != GraphProjectionKind.Scalar))
        { throw new GraphPlanningException("COHDBG001", ErrorMessages.ExecuteScalarRequired, plan.Query.Location); }
        if (plan.Writes)
        {
            await database.LockWriterAsync(operation, token).ConfigureAwait(false);
            // Bindings have already been normalized and snapshotted. Re-normalizing would strip
            // another sigil from an unused key such as "$$unused" and can invent a collision.
            plan = new GraphPlanner(database, operation.Context.Snapshot).Plan(statement.GqlExpression, parameters, paths, token, normalizedParameters: true);
        }
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
                        { binding.Add(assignment.Alias!, GraphExpressionEvaluator.Evaluate(assignment.Expression, binding, operation.EvaluationContext, parameters)); }
                    }
                    break;
                case GqlFilterStage filter:
                    bindings = bindings.Where(binding =>
                    {
                        token.ThrowIfCancellationRequested();
                        return GraphExpressionEvaluator.Boolean(GraphExpressionEvaluator.Evaluate(filter.Predicate, binding, operation.EvaluationContext, parameters), filter.Predicate) is true;
                    }).ToList();
                    break;
                case GqlForStage loop:
                    var expandedRows = new List<Dictionary<string, object?>>();
                    foreach (var binding in bindings)
                    {
                        token.ThrowIfCancellationRequested();
                        object? value = GraphExpressionEvaluator.Evaluate(loop.List, binding, operation.EvaluationContext, parameters);
                        if (value is null) { continue; }
                        if (value is not System.Collections.IList list || value is byte[])
                        { throw new GraphPlanningException("COHDBG013", ErrorMessages.ForListRequired, loop.List.Location); }
                        foreach (object? item in list)
                        {
                            token.ThrowIfCancellationRequested();
                            var kind = plan.Stages[stageIndex].ElementKind;
                            if (item is not null && (kind == GraphProjectionKind.Node && item is not GraphNode ||
                                kind == GraphProjectionKind.Relationship && item is not GraphRelationship))
                            { throw new GraphPlanningException("COHDBG013", ErrorMessages.EntityBindingRequired, loop.List.Location); }
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
                    affected += await DeleteAsync(database, operation, deletion, bindings, token).ConfigureAwait(false);
                    break;
                case GqlSetStage or GqlRemoveStage:
                    await operation.Updates.ApplyAsync(stage, bindings, parameters, token).ConfigureAwait(false);
                    break;
                case GqlReturnStage projection when stageIndex != plan.Stages.Count - 1:
                    bindings = ProjectBindings(stagePlan, projection, bindings, operation.EvaluationContext, parameters, token);
                    break;
                case GqlNextStage when plan.Stages[stageIndex - 1].Stage is not GqlReturnStage:
                    bindings = [new(StringComparer.Ordinal)];
                    break;
            }
        }
        affected += await operation.Updates.PublishAsync(token).ConfigureAwait(false);
        if (plan.Projections.Count == 0) { return new GraphMutationResult(affected, plan.Warnings); }
        if (plan.Grouping is { } grouping) { bindings = GraphGrouping.Apply(grouping, bindings, operation.EvaluationContext, parameters, token); }
        var columns = plan.Projections.Select((projection, i) => new DatabaseColumn
        { Name = projection.Name, Ordinal = i, Type = projection.Type }).ToArray();
        var rows = new List<GraphOrderedRow>(bindings.Count);
        var distinct = plan.Distinct is null ? null : new HashSet<object?[]>(GraphRowComparer.Instance);
        bool valueColumnsAdded = false;
        foreach (var binding in bindings)
        {
            token.ThrowIfCancellationRequested();
            var row = plan.Projections.Select(projection => GraphExpressionEvaluator.Evaluate(projection.Expression, binding, operation.EvaluationContext, parameters)).ToArray();
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
            { rows.Add(new GraphOrderedRow(row, GraphSortPage.Keys(plan, binding, row, operation.EvaluationContext, token), rows.Count)); }
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
        return new GraphQueryResult(columns, paged, plan.Warnings, compositeColumns, plan.Writes ? affected : -1);
    }

    internal static async ValueTask<long> DeleteAsync(GraphDatabase database, GraphOperation operation, GqlDeleteStage deletion,
        List<Dictionary<string, object?>> bindings, CancellationToken token)
    {
        // Validate every target before deleting anything, including carried runtime values.
        var relationships = new HashSet<GraphRelationshipId>();
        var nodes = new HashSet<GraphNodeId>();
        foreach (var binding in bindings)
        {
            token.ThrowIfCancellationRequested();
            foreach (string variable in deletion.Variables)
            {
                if (!binding.TryGetValue(variable, out var value))
                { throw new GraphPlanningException("COHDBG013", ErrorMessages.EntityBindingRequired, deletion.Location); }
                if (value is GraphNode node) { nodes.Add(node.Id); }
                else if (value is GraphRelationship relationship) { relationships.Add(relationship.Id); }
                else if (value is not null)
                { throw new GraphPlanningException("COHDBG013", ErrorMessages.EntityBindingRequired, deletion.Location); }
            }
        }
        long affected = 0;
        // Delete relationships first, so explicit DELETE r,a can remove a connected node.
        foreach (var id in relationships)
        {
            if (operation.Updates.FindRelationship(id.Value) is not null) { affected++; }
            if (!operation.Updates.IsCreated(false, id.Value))
            { await database.Store.DeleteRelationshipAsync(id.Value, operation.Context, token).ConfigureAwait(false); }
            operation.Updates.DeletedRelationship(id.Value);
        }
        foreach (var id in nodes)
        {
            if (operation.Updates.FindNode(id.Value) is not null) { affected++; }
            var incident = await operation.Updates.GetIncidentAsync(id.Value, token).ConfigureAwait(false);
            if (!deletion.Detach && incident.Any(edge => operation.Updates.IsCreated(false, edge.Id)))
            { throw new InvalidOperationException(ErrorMessages.ConnectedNodeDeletion); }
            foreach (var edge in incident) { operation.Updates.DeletedRelationship(edge.Id); }
            if (!operation.Updates.IsCreated(true, id.Value))
            { await database.Store.DeleteNodeAsync(id.Value, deletion.Detach, operation.Context, token).ConfigureAwait(false); }
            operation.Updates.DeletedNode(id.Value);
        }
        foreach (var binding in bindings) { foreach (string variable in deletion.Variables) { binding.Remove(variable); } }
        return affected;
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
            var candidates = new List<MatchCandidate>();
            if (!clause.MatchesNothing)
            {
                candidates.Add(new MatchCandidate(binding, null));
                foreach (var path in clause.Paths)
                {
                    var expanded = new List<MatchCandidate>();
                    foreach (var candidate in candidates)
                    {
                        await MatchCoreAsync(database, operation, path, candidate.Bindings, (row, properties) =>
                        {
                            var deferred = candidate.Deferred;
                            for (var property = properties; property is not null; property = property.Previous)
                            { deferred = DeferProperties(property.Expected, property.Actual, row, deferred); }
                            AddBinding(expanded, new MatchCandidate(row, deferred));
                        }, budget, parameters, token, deferUnbound: true).ConfigureAwait(false);
                    }
                    candidates = expanded;
                }
            }
            bool found = false;
            foreach (var candidate in candidates)
            {
                token.ThrowIfCancellationRequested();
                // Cross-part maps include anonymous elements, whose stored values travel
                // with the row until the complete clause can evaluate their predicates.
                if (!DeferredPropertiesMatch(candidate, operation.EvaluationContext, parameters)) { continue; }
                if (clause.Predicate is not null && GraphExpressionEvaluator.Boolean(
                    GraphExpressionEvaluator.Evaluate(clause.Predicate, candidate.Bindings, operation.EvaluationContext, parameters), clause.Predicate) is not true) { continue; }
                AddBinding(matched, candidate.Bindings);
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

    private sealed record MatchCandidate(Dictionary<string, object?> Bindings, DeferredProperty? Deferred);
    private sealed record DeferredProperty(GqlExpression Expression, object? Value, bool Exists, DeferredProperty? Previous);

    private static DeferredProperty? DeferProperties(IReadOnlyDictionary<string, GqlExpression> expected,
        IReadOnlyDictionary<string, object?> actual, IReadOnlyDictionary<string, object?> bindings, DeferredProperty? deferred)
    {
        foreach (var property in expected)
        {
            if (!GraphValueExpressions.ReferencesUnbound(property.Value, bindings)) { continue; }
            bool exists = actual.TryGetValue(property.Key, out var value);
            deferred = new DeferredProperty(property.Value, value, exists, deferred);
        }
        return deferred;
    }

    private static bool DeferredPropertiesMatch(MatchCandidate candidate, GraphEvaluationContext context, IReadOnlyDictionary<string, object?>? parameters)
    {
        for (var property = candidate.Deferred; property is not null; property = property.Previous)
        {
            if (!property.Exists || !GraphExpressionEvaluator.Equal(
                GraphExpressionEvaluator.PropertyScalar(property.Expression, candidate.Bindings, context, parameters), property.Value)) { return false; }
        }
        return true;
    }

    private static List<Dictionary<string, object?>> ProjectBindings(GraphPlan plan, GqlReturnStage stage,
        List<Dictionary<string, object?>> bindings, GraphEvaluationContext context, IReadOnlyDictionary<string, object?>? parameters, CancellationToken token)
    {
        if (plan.Grouping is { } grouping) { bindings = GraphGrouping.Apply(grouping, bindings, context, parameters, token); }
        var rows = new List<GraphOrderedRow>(bindings.Count);
        var scopes = new Dictionary<object?[], Dictionary<string, object?>>();
        var distinct = plan.Distinct is null ? null : new HashSet<object?[]>(GraphRowComparer.Instance);
        foreach (var binding in bindings)
        {
            token.ThrowIfCancellationRequested();
            var values = plan.Projections.Select(projection => GraphExpressionEvaluator.Evaluate(projection.Expression, binding, context, parameters)).ToArray();
            if (distinct is not null && !distinct.Add(values)) { continue; }
            var scope = plan.Grouping is null && plan.Distinct is null
                ? new Dictionary<string, object?>(binding, StringComparer.Ordinal) : new Dictionary<string, object?>(StringComparer.Ordinal);
            for (int i = 0; i < values.Length; i++)
            { if (plan.Projections[i].BindingName is { } name) { scope[name] = values[i]; } }
            scopes.Add(values, scope);
            rows.Add(new GraphOrderedRow(values, GraphSortPage.Keys(plan, binding, values, context, token), rows.Count));
        }
        var paged = GraphSortPage.Apply(rows, plan.SortPage, token);
        var result = new List<Dictionary<string, object?>>(paged.Length);
        foreach (var values in paged)
        {
            token.ThrowIfCancellationRequested();
            if (stage.Predicate is { } predicate && GraphExpressionEvaluator.Boolean(
                GraphExpressionEvaluator.Evaluate(predicate, scopes[values], context, parameters), predicate) is not true) { continue; }
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

    internal static ValueTask MatchAsync(GraphDatabase database, GraphOperation operation, GraphPathPlan plan,
        Dictionary<string, object?> input, List<Dictionary<string, object?>> output, ExpansionBudget budget, IReadOnlyDictionary<string, object?>? parameters, CancellationToken token)
        => MatchCoreAsync(database, operation, plan, input, (bindings, _) => AddBinding(output, bindings), budget, parameters, token);

    private static void AddBinding<T>(List<T> output, T binding)
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

    private static bool AcceptNode(GqlNodePattern pattern, GraphNode node, Dictionary<string, object?> bindings, GraphEvaluationContext context, IReadOnlyDictionary<string, object?>? parameters, bool deferUnbound = false)
        => GraphLabelEvaluator.Accepts(pattern, node.Labels) && Accept(pattern.Variable, node, bindings) &&
            PropertiesMatch(pattern.Properties, node.Properties, bindings, context, parameters, deferUnbound);
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
    private static bool PropertiesMatch(IReadOnlyDictionary<string, GqlExpression> expected, IReadOnlyDictionary<string, object?> actual, IReadOnlyDictionary<string, object?> bindings, GraphEvaluationContext context, IReadOnlyDictionary<string, object?>? parameters, bool deferUnbound = false)
        => expected.All(item => deferUnbound && GraphValueExpressions.ReferencesUnbound(item.Value, bindings) ||
            actual.TryGetValue(item.Key, out var value) && GraphExpressionEvaluator.Equal(GraphExpressionEvaluator.PropertyScalar(item.Value, bindings, context, parameters), value));

    private static async ValueTask<long> CreateAsync(GraphDatabase database, GraphOperation operation, GqlPathPattern path,
        Dictionary<string, object?> bindings, IReadOnlyDictionary<string, object?>? parameters, CancellationToken token)
    {
        long affected = 0;
        _ = GraphPathShape.Create(path, creating: true);
        var nodes = new GraphNode[path.Nodes.Count];
        for (int i = 0; i < nodes.Length; i++)
        {
            var pattern = path.Nodes[i];
            if (pattern.Variable is { } variable && bindings.TryGetValue(variable, out var bound))
            {
                if (bound is not GraphNode node || !AcceptNode(pattern, node, bindings, operation.EvaluationContext, parameters))
                { throw new GraphPlanningException("COHDBG003", ErrorMessages.InsertEndpointInvalid); }
                nodes[i] = node;
            }
            else
            {
                nodes[i] = await operation.Updates.CreateNodeAsync(GraphLabelEvaluator.InsertLabels(pattern), EvaluateProperties(pattern.Properties, bindings, operation.EvaluationContext, parameters), token).ConfigureAwait(false);
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
            var relationship = await operation.Updates.CreateRelationshipAsync(nodes[outgoing ? i : i + 1].Id,
                nodes[outgoing ? i + 1 : i].Id, GraphLabelEvaluator.InsertType(pattern), EvaluateProperties(pattern.Properties, bindings, operation.EvaluationContext, parameters), token).ConfigureAwait(false);
            Accept(pattern.Variable, relationship, bindings);
            affected++;
        }
        return affected;
    }

    private static IReadOnlyDictionary<string, object?> EvaluateProperties(
        IReadOnlyDictionary<string, GqlExpression> properties, IReadOnlyDictionary<string, object?> bindings, GraphEvaluationContext context, IReadOnlyDictionary<string, object?>? parameters)
        => properties.ToDictionary(item => item.Key, item => GraphExpressionEvaluator.PropertyScalar(item.Value, bindings, context, parameters), StringComparer.Ordinal);

    internal sealed class ExpansionBudget
    {
        internal const int PhysicalLimit = 1_000_000;
        private int _physical;
        private int _viable;
        private int _candidates;
        internal int Consumed => _physical;
        // Sampling after incident loading isolates the retained DFS state from read buffers.
        internal Action<int>? OnCandidate { get; init; }
        internal void VisitCandidate() => OnCandidate?.Invoke(++_candidates);
        internal int Physical => _physical;
        internal int Viable => _viable;
        internal void ConsumeViable() => _viable++;
        internal Action<int>? OnConsume { get; init; }
        internal void Consume()
        {
            OnConsume?.Invoke(Consumed + 1);
            if (++_physical > PhysicalLimit) { throw new GraphPlanningException("COHDBG004", string.Format(CultureInfo.CurrentCulture, ErrorMessages.ExpansionLimitExceeded, Physical, Viable)); }
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

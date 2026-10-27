using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Assimalign.Cohesion.Database.Execution;
using Assimalign.Cohesion.Database.Execution.Internal;
using Assimalign.Cohesion.Database.Graph.Language;
using Assimalign.Cohesion.Database.Types;

using Assimalign.Cohesion.Database.Graph.Protocol;

namespace Assimalign.Cohesion.Database.Graph.Internal;

internal static partial class GraphPlanExecutor
{
    internal static async ValueTask<DatabaseResult> ExecuteAsync(GraphDatabase database, GraphOperation operation,
        GqlQueryStatement statement, IReadOnlyDictionary<string, object?>? parameters, CancellationToken token, bool paths = false, bool scalarsOnly = false, bool compositeValues = true)
    {
        operation.EnsureActive();
        var error = statement.Diagnostics.FirstOrDefault(item => item.Severity == DiagnosticSeverity.Error);
        if (error is not null)
        {
            if (error.Code == GraphStatementTooComplex.ParserCode) { throw GraphStatementTooComplex.FromParse(error); }
            throw new DatabaseParseException(string.Format(System.Globalization.CultureInfo.CurrentCulture, ErrorMessages.GraphQueryRequestFromGqlDatabaseParse, error.Code, error.Message), statement.Diagnostics);
        }
        if (paths && statement.GqlExpression.CatalogSurface is not null)
        { throw new GraphPlanningException("CG05001", ErrorMessages.PathProjectionRequired, statement.GqlExpression.Location); }
        if (statement.GqlExpression.CatalogSurface is { } surface)
        {
            // Catalog requests have no parameter operands, but the shared contract still rejects
            // ambiguous normalized keys, including unused ones, on every execution.
            ParameterBindings.Normalize(parameters, name => new GraphPlanningException("CG01014",
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
        { throw new GraphPlanningException("CG02013", ErrorMessages.CompositeLimit, plan.Query.Location); }
        if (!compositeValues && compositeColumns.Any(value => value))
        { throw new GraphPlanningException("CG07016", ErrorMessages.CompositeCapabilityRequired, plan.Query.Location); }
        if (scalarsOnly && plan.Projections.Any(projection => projection.Kind == GraphProjectionKind.EntityValue && projection.MayBeComposite))
        { throw new GraphPlanningException("CG05001", ErrorMessages.EntityListWireUnsupported, plan.Query.Location); }
        if (scalarsOnly && plan.Projections.Any(projection => projection.Kind != GraphProjectionKind.Scalar))
        { throw new GraphPlanningException("CG05001", ErrorMessages.ExecuteScalarRequired, plan.Query.Location); }
        if (plan.Writes)
        {
            await database.LockWriterAsync(operation, token).ConfigureAwait(false);
            // Bindings have already been normalized and snapshotted. Re-normalizing would strip
            // another sigil from an unused key such as "$$unused" and can invent a collision.
            plan = new GraphPlanner(database, operation.Context.Snapshot).Plan(statement.GqlExpression, parameters, paths, token, normalizedParameters: true);
        }
        operation.FunctionBindings = plan.FunctionBindings;
        operation.FunctionCancellation = token;
        if (!plan.Writes)
        {
            return await ExecuteReadAsync(database, operation, plan, parameters, token,
                paths, scalarsOnly, compositeValues).ConfigureAwait(false);
        }
        var budget = operation.QueryBudget ??= new ExpansionBudget();
        using var executionMemory = budget.Memory.Reserve(512);
        var bindings = new List<Dictionary<string, object?>> { new(StringComparer.Ordinal) };
        var bindingMemory = new GraphMemoryOwner(budget.Memory);
        bindingMemory.Add(256);
        bindingMemory.RetainValue(bindings[0], token);
        using var layoutMemory = new GraphMemoryOwner(budget.Memory);
        var layout = new GraphBindingLayout(layoutMemory);
        long affected = 0;
        try
        {
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
                    {
                        await ReplaceBindingsAsync(StreamMatchClauseAsync(database, operation, clause,
                            StreamBindingsAsync(bindings, token), layout, budget, parameters, token)).ConfigureAwait(false);
                    }
                    break;
                case GqlLetStage or GqlFilterStage or GqlForStage:
                    await ReplaceBindingsAsync(StreamStageAsync(plan.Stages[stageIndex], StreamBindingsAsync(bindings, token),
                        layout, budget, operation, parameters, token)).ConfigureAwait(false);
                    break;
                case GqlInsertStage insertion:
                    foreach (var binding in bindings)
                    {
                        token.ThrowIfCancellationRequested();
                        bindingMemory.ReleaseValue(binding);
                        foreach (var path in insertion.Paths)
                        { affected += await CreateAsync(database, operation, path, binding, parameters, token).ConfigureAwait(false); }
                        bindingMemory.RetainValue(binding, token);
                    }
                    break;
                case GqlDeleteStage deletion:
                    affected += await DeleteAsync(database, operation, deletion, bindings, token, bindingMemory).ConfigureAwait(false);
                    break;
                case GqlSetStage or GqlRemoveStage:
                    await operation.Updates.ApplyAsync(stage, bindings, parameters, token, bindingMemory).ConfigureAwait(false);
                    break;
                case GqlReturnStage projection when stageIndex != plan.Stages.Count - 1:
                    await ReplaceBindingsAsync(StreamBoundaryAsync(stagePlan, projection, StreamBindingsAsync(bindings, token),
                        layout, budget, operation, parameters, token)).ConfigureAwait(false);
                    break;
                case GqlNextStage when plan.Stages[stageIndex - 1].Stage is not GqlReturnStage:
                    await ReplaceBindingsAsync(SeedBindingAsync(layout, budget, token)).ConfigureAwait(false);
                    break;
            }
        }
        affected += await operation.Updates.PublishAsync(token).ConfigureAwait(false);
        if (plan.Projections.Count == 0) { return new GraphMutationResult(affected, plan.Warnings); }
        return await ProjectResultAsync(database, operation, plan, StreamBindingsAsync(bindings, token),
            parameters, token, paths, scalarsOnly, compositeValues, affected).ConfigureAwait(false);
        }
        finally { bindings.Clear(); bindingMemory.Dispose(); }

        async ValueTask ReplaceBindingsAsync(IAsyncEnumerable<IReadOnlyDictionary<string, object?>> source)
        {
            var nextMemory = new GraphMemoryOwner(budget.Memory);
            var next = new List<Dictionary<string, object?>>();
            try
            {
                await foreach (var binding in source.ConfigureAwait(false))
                {
                    token.ThrowIfCancellationRequested();
                    var copy = new Dictionary<string, object?>(binding, StringComparer.Ordinal);
                    nextMemory.Add(64);
                    nextMemory.RetainValue(copy, token);
                    next.Add(copy);
                }
                bindings.Clear();
                bindings = next;
                bindingMemory.Dispose();
                bindingMemory = nextMemory;
            }
            catch { next.Clear(); nextMemory.Dispose(); throw; }
        }
    }

    internal static async ValueTask<long> DeleteAsync(GraphDatabase database, GraphOperation operation, GqlDeleteStage deletion,
        List<Dictionary<string, object?>> bindings, CancellationToken token, GraphMemoryOwner? bindingMemory = null)
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
                { throw new GraphPlanningException("CG02013", ErrorMessages.EntityBindingRequired, deletion.Location); }
                if (value is GraphNode node) { nodes.Add(node.Id); }
                else if (value is GraphRelationship relationship) { relationships.Add(relationship.Id); }
                else if (value is not null)
                { throw new GraphPlanningException("CG02013", ErrorMessages.EntityBindingRequired, deletion.Location); }
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
        foreach (var binding in bindings)
        {
            bindingMemory?.ReleaseValue(binding);
            foreach (string variable in deletion.Variables) { binding.Remove(variable); }
            bindingMemory?.RetainValue(binding, token);
        }
        return affected;
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

    internal static async ValueTask MatchAsync(GraphDatabase database, GraphOperation operation, GraphPathPlan plan,
        Dictionary<string, object?> input, List<Dictionary<string, object?>> output, ExpansionBudget budget, IReadOnlyDictionary<string, object?>? parameters, CancellationToken token)
    {
        await foreach (var match in MatchCoreStreamAsync(database, operation, plan, input, budget, parameters, token).ConfigureAwait(false))
        { output.Add(match.Bindings); }
    }

    private static GraphPath ProjectRelationship(GraphDatabase database, GraphOperation operation, GraphRelationship relationship)
    {
        // Resolve the bound relationship's endpoints in the same statement snapshot, including anonymous nodes.
        // This projects an engine entity binding directly; scalar projection rows are never involved.
        var snapshot = operation.Context.Snapshot;
        if (database.Store.FindNode(relationship.From.Value, snapshot) is not { } from ||
            database.Store.FindNode(relationship.To.Value, snapshot) is not { } to)
        { throw new GraphPlanningException("CG03003", ErrorMessages.ProjectRelationshipGraphPlanning); }
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
                { throw new GraphPlanningException("CG03003", ErrorMessages.InsertEndpointInvalid); }
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
            { throw new GraphPlanningException("CG05001", ErrorMessages.PlanExecutorCreateAsyncGraphPlanning); }
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

    [global::System.Diagnostics.DebuggerDisplay("ExpansionBudget")]
    [global::System.Diagnostics.DebuggerTypeProxy(typeof(global::Assimalign.Cohesion.Database.Graph.Internal.GraphPlanExecutor.ExpansionBudget.DebugView))]
    internal sealed class ExpansionBudget
    {
        internal const int PhysicalLimit = 1_000_000;
        private long _physical;
        private long _viable;
        private int _candidates;
        private BudgetObservations? _observations;
        internal ExpansionBudget(long workLimit = PhysicalLimit, long memoryLimit = 256L * 1024 * 1024, GraphQueryMemoryPool? pool = null)
        {
            WorkLimit = workLimit;
            Memory = new(memoryLimit, pool);
        }
        internal long WorkLimit { get; }
        internal GraphQueryMemory Memory { get; }
        internal long SelectorPairs
        { get => (_observations as SelectorObservations)?.Pairs ?? 0; set => ObserveSelector().Pairs = value; }
        internal long SelectorStates
        { get => (_observations as SelectorObservations)?.States ?? 0; set => ObserveSelector().States = value; }
        internal long SelectorCandidates
        { get => (_observations as SelectorObservations)?.Candidates ?? 0; set => ObserveSelector().Candidates = value; }
        internal long SelectorSelected
        { get => (_observations as SelectorObservations)?.Selected ?? 0; set => ObserveSelector().Selected = value; }
        internal long Consumed => _physical;
        // Observe every pending DFS state before it is popped, including parents and children.
        internal Action<int>? OnFrontier
        { get => _observations?.OnFrontier; init => (_observations ??= new()).OnFrontier = value; }
        // Sampling at each consumed candidate includes the bounded incident read buffers.
        internal Action<int>? OnCandidate
        { get => _observations?.OnCandidate; init => (_observations ??= new()).OnCandidate = value; }
        internal void VisitCandidate() => OnCandidate?.Invoke(++_candidates);
        internal long Physical => _physical;
        internal long Viable => _viable;
        internal void ConsumeViable() => _viable++;
        internal Action<int>? OnConsume
        { get => _observations?.OnConsume; init => (_observations ??= new()).OnConsume = value; }

        private SelectorObservations ObserveSelector()
        {
            if (_observations is SelectorObservations selector) { return selector; }
            selector = new SelectorObservations
            {
                OnFrontier = _observations?.OnFrontier, OnCandidate = _observations?.OnCandidate,
                OnConsume = _observations?.OnConsume,
            };
            _observations = selector;
            return selector;
        }
        internal void EnsureAvailable()
        {
            if (_physical >= WorkLimit)
            { throw new GraphPlanningException("CG05004", string.Format(CultureInfo.CurrentCulture, ErrorMessages.ExpansionLimitExceeded, Physical, Viable, WorkLimit)); }
        }
        internal void Consume()
        {
            bool overflow = _physical == long.MaxValue;
            long next = overflow ? long.MaxValue : _physical + 1;
            OnConsume?.Invoke((int)Math.Min(next, int.MaxValue));
            _physical = next;
            if (overflow || _physical > WorkLimit)
            { throw new GraphPlanningException("CG05004", string.Format(CultureInfo.CurrentCulture, ErrorMessages.ExpansionLimitExceeded, Physical, Viable, WorkLimit)); }
        }

        private class BudgetObservations
        {
            public Action<int>? OnFrontier { get; set; }
            public Action<int>? OnCandidate { get; set; }
            public Action<int>? OnConsume { get; set; }
        }

        private sealed class SelectorObservations : BudgetObservations
        {
            public long Pairs { get; set; }
            public long States { get; set; }
            public long Candidates { get; set; }
            public long Selected { get; set; }
        }

        private sealed class DebugView
        {
            public DebugView(global::Assimalign.Cohesion.Database.Graph.Internal.GraphPlanExecutor.ExpansionBudget value)
            {
                _ = value;
            }
        }
    }
}

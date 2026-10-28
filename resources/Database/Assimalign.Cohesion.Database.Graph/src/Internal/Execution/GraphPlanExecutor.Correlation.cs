using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using Assimalign.Cohesion.Database.Graph.Language;
using Assimalign.Cohesion.Database.Graph.Protocol;

namespace Assimalign.Cohesion.Database.Graph.Internal;

internal static partial class GraphPlanExecutor
{
    private sealed record BoundPathSearch : PathSearch
    {
        internal BoundPathSearch(GraphPathPlan plan, GraphPath value) : base(plan) { Value = value; }
        public GraphPath Value { get; }
    }

    private static bool SamePath(GraphPath path, IReadOnlyList<GraphNode> nodes, IReadOnlyList<GraphRelationship> edges)
        => path.Nodes.Count == nodes.Count && path.Relationships.Count == edges.Count &&
            path.Nodes.Select(node => node.Id).SequenceEqual(nodes.Select(node => node.Id)) &&
            path.Relationships.Select(edge => edge.Id).SequenceEqual(edges.Select(edge => edge.Id));

    private static async IAsyncEnumerable<PathMatch> BoundPathStreamAsync(GraphDatabase database, GraphOperation operation,
        GraphPathPlan plan, Dictionary<string, object?> input, ExpansionBudget budget,
        IReadOnlyDictionary<string, object?>? parameters, [EnumeratorCancellation] CancellationToken token, bool deferUnbound)
    {
        token.ThrowIfCancellationRequested();
        if (input[plan.Pattern.Variable!] is null) { yield break; }
        if (input[plan.Pattern.Variable!] is not GraphPath value)
        { throw new GraphPlanningException("CG02016", ErrorMessages.CorrelatedEntityRequired); }
        using var memory = budget.Memory.Reserve(256 + 8L * value.Relationships.Count);
        var bound = new BoundPathSearch(plan, value)
        {
            Anchor = new GraphAnchor(0, null, null, null), Start = value.Nodes[0].Id.Value,
            End = value.Nodes[^1].Id.Value, HopLimit = value.Relationships.Count, ExactLength = true,
            Identity = value.Relationships.Select(edge => edge.Id.Value).ToArray(),
        };
        await foreach (var match in SearchPathStreamAsync(database, operation, bound, input, budget, parameters, token, deferUnbound).ConfigureAwait(false))
        { yield return match; }
    }

    // No cardinality limit belongs here: collection consumers must be able to drain this same
    // boundary. Rows are borrowed until the next advance; their bindings never escape to callers.
    internal static async IAsyncEnumerable<IReadOnlyDictionary<string, object?>> ExecuteCorrelatedAsync(
        GraphDatabase database, GraphOperation operation, GraphCorrelatedSubplan subplan,
        IReadOnlyDictionary<string, object?> arguments, IReadOnlyDictionary<string, object?>? parameters,
        [EnumeratorCancellation] CancellationToken token)
    {
        RuntimeHelpers.EnsureSufficientExecutionStack();
        token.ThrowIfCancellationRequested();
        operation.EnsureActive();
        foreach (string name in subplan.CorrelatedVariables)
        {
            if (!arguments.ContainsKey(name))
            { throw new GraphPlanningException("CG05027", ErrorMessages.CorrelatedPlanRequired); }
        }
        foreach (var argument in subplan.PatternArguments)
        {
            if (!arguments.TryGetValue(argument.Key, out var value))
            { throw new GraphPlanningException("CG05027", ErrorMessages.CorrelatedPlanRequired); }
            if (value is null) { yield break; }
            if (argument.Value switch
                { GraphProjectionKind.Node => value is not GraphNode, GraphProjectionKind.Relationship => value is not GraphRelationship,
                    GraphProjectionKind.Path => value is not GraphPath, _ => false })
            { throw new GraphPlanningException("CG02016", ErrorMessages.CorrelatedEntityRequired); }
        }
        var budget = operation.QueryBudget ??= new ExpansionBudget();
        using var memory = new GraphMemoryOwner(budget.Memory);
        var layout = new GraphBindingLayout(memory);
        IAsyncEnumerable<IReadOnlyDictionary<string, object?>> source = CorrelatedArgumentAsync(subplan, arguments, budget, token);
        foreach (var clause in subplan.Plan.MatchClauses)
        { source = StreamMatchClauseAsync(database, operation, clause, source, layout, budget, parameters, token); }
        await foreach (var binding in source.ConfigureAwait(false))
        {
            token.ThrowIfCancellationRequested();
            yield return binding;
        }
    }

    private static async IAsyncEnumerable<IReadOnlyDictionary<string, object?>> CorrelatedArgumentAsync(
        GraphCorrelatedSubplan subplan, IReadOnlyDictionary<string, object?> arguments, ExpansionBudget budget, [EnumeratorCancellation] CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        using var memory = budget.Memory.Reserve(128 + 80L * subplan.CorrelatedVariables.Count);
        // A sort/group binding may still carry hidden incoming names for other expressions.
        // Only planned arguments cross this lexical boundary; inner declarations stay local.
        var binding = new Dictionary<string, object?>(subplan.CorrelatedVariables.Count, StringComparer.Ordinal);
        foreach (string name in subplan.CorrelatedVariables)
        { token.ThrowIfCancellationRequested(); binding.Add(name, arguments[name]); }
        yield return binding;
        await Task.CompletedTask.ConfigureAwait(false);
    }

    internal static async ValueTask<bool> ExistsAsync(GraphDatabase database, GraphOperation operation,
        GraphCorrelatedSubplan subplan, IReadOnlyDictionary<string, object?> arguments,
        IReadOnlyDictionary<string, object?>? parameters, CancellationToken token)
    {
        var inner = ExecuteCorrelatedAsync(database, operation, subplan, arguments, parameters, token).GetAsyncEnumerator(token);
        var registry = operation.FunctionBindings as GraphCorrelatedBindings;
        try
        {
            registry?.OnInnerAdvance?.Invoke();
            return await inner.MoveNextAsync().ConfigureAwait(false);
        }
        finally
        {
            await inner.DisposeAsync().ConfigureAwait(false);
            registry?.OnInnerDisposed?.Invoke();
        }
    }
}

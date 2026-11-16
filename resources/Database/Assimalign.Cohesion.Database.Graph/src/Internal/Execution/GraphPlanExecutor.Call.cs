using System.Collections.Generic;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;

namespace Assimalign.Cohesion.Database.Graph.Internal;

internal static partial class GraphPlanExecutor
{
    private static async IAsyncEnumerable<IReadOnlyDictionary<string, object?>> StreamCallAsync(GraphDatabase database,
        GraphOperation operation, GraphCallStagePlan call, IAsyncEnumerable<IReadOnlyDictionary<string, object?>> input,
        GraphBindingLayout layout, ExpansionBudget budget, [EnumeratorCancellation] CancellationToken token)
    {
        RuntimeHelpers.EnsureSufficientExecutionStack();
        using var iteratorMemory = budget.Memory.Reserve(256);
        var parentBindings = operation.FunctionBindings;
        try
        {
            await foreach (var binding in input.ConfigureAwait(false))
            {
                token.ThrowIfCancellationRequested(); operation.EnsureActive(); budget.Consume();
                var imports = new Dictionary<string, object?>(System.StringComparer.Ordinal);
                foreach (string name in call.Imports) { imports.Add(name, binding[name]); }
                using var argumentsMemory = new GraphMemoryOwner(budget.Memory);
                argumentsMemory.RetainValue(imports, token);
                IReadOnlyDictionary<string, object?>[] incoming = [imports];
                using var inputMemory = budget.Memory.Reserve(32 + 8);
                var source = call.Composite is { } composite
                    ? StreamCompositeRowsAsync(database, operation, composite, token, incoming, globals: imports)
                    : StreamCompositeOperandAsync(database, operation, call.Inner, token, incoming, imports);
                await using var cursor = source.GetAsyncEnumerator(token);
                bool found = false;
                while (true)
                {
                    operation.FunctionBindings = call.Inner.FunctionBindings;
                    bool moved;
                    try { moved = await cursor.MoveNextAsync().ConfigureAwait(false); }
                    finally { operation.FunctionBindings = parentBindings; }
                    if (!moved) { break; }
                    token.ThrowIfCancellationRequested(); operation.EnsureActive(); budget.Consume();
                    found = true;
                    var values = cursor.Current;
                    var row = new GraphBindingRow(layout, binding);
                    for (int i = 0; i < values.Length; i++) { row.Add(call.Plan.Projections[i].BindingName!, values[i]); }
                    using var valuesMemory = new GraphMemoryOwner(budget.Memory);
                    valuesMemory.RetainValue(values, token);
                    using (budget.Memory.Reserve(row.RetainedBytes)) { yield return row; }
                }
                if (!found && call.Call.Optional)
                {
                    token.ThrowIfCancellationRequested(); operation.EnsureActive(); budget.Consume();
                    var row = new GraphBindingRow(layout, binding);
                    foreach (var projection in call.Plan.Projections) { row.Add(projection.BindingName!, null); }
                    using (budget.Memory.Reserve(row.RetainedBytes)) { yield return row; }
                }
            }
        }
        finally { operation.FunctionBindings = parentBindings; }
    }

    private static async IAsyncEnumerable<IReadOnlyDictionary<string, object?>> RestoreCallImportsAsync(
        IAsyncEnumerable<IReadOnlyDictionary<string, object?>> input, IReadOnlyDictionary<string, object?> globals,
        GraphBindingLayout layout, ExpansionBudget budget, [EnumeratorCancellation] CancellationToken token)
    {
        await foreach (var binding in input.ConfigureAwait(false))
        {
            token.ThrowIfCancellationRequested();
            if (globals.Count == 0) { yield return binding; continue; }
            if (binding is GraphGroupedBinding grouped)
            {
                var combined = new GraphGroupedBinding(grouped.ValuesByExpression);
                foreach (var pair in binding) { combined[pair.Key] = pair.Value; }
                foreach (var pair in globals) { combined[pair.Key] = pair.Value; }
                using (budget.Memory.Reserve(256 + 80L * combined.Count)) { yield return combined; }
                continue;
            }
            var row = new GraphBindingRow(layout, binding);
            foreach (var pair in globals) { row[pair.Key] = pair.Value; }
            using (budget.Memory.Reserve(row.RetainedBytes)) { yield return row; }
        }
    }
}

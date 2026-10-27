using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Runtime.ExceptionServices;
using System.Threading;
using System.Threading.Tasks;

using Assimalign.Cohesion.Database.Execution;
using Assimalign.Cohesion.Database.Graph.Language;
using Assimalign.Cohesion.Database.Graph.Protocol;
using Assimalign.Cohesion.Database.Types;

namespace Assimalign.Cohesion.Database.Graph.Internal;

internal static partial class GraphPlanExecutor
{
    private static async ValueTask<DatabaseResult> ExecuteReadAsync(GraphDatabase database, GraphOperation operation,
        GraphPlan plan, IReadOnlyDictionary<string, object?>? parameters, CancellationToken token,
        bool paths, bool scalarsOnly, bool compositeValues)
    {
        var budget = operation.QueryBudget ??= new ExpansionBudget();
        using var executionMemory = budget.Memory.Reserve(512);
        using var layoutMemory = new GraphMemoryOwner(budget.Memory);
        var layout = new GraphBindingLayout(layoutMemory);
        IAsyncEnumerable<IReadOnlyDictionary<string, object?>> source = SeedBindingAsync(layout, budget, token);
        for (int i = 0; i < plan.Stages.Count; i++)
        {
            token.ThrowIfCancellationRequested();
            var stage = plan.Stages[i];
            if (stage.Stage is GqlReturnStage && i == plan.Stages.Count - 1) { continue; }
            if (stage.Stage is GqlMatchStage)
            {
                foreach (var clause in stage.Plan.MatchClauses)
                { source = StreamMatchClauseAsync(database, operation, clause, source, layout, budget, parameters, token); }
            }
            else if (stage.Stage is GqlReturnStage projection)
            { source = StreamBoundaryAsync(stage.Plan, projection, source, layout, budget, operation, parameters, token); }
            else if (stage.Stage is GqlNextStage)
            {
                if (i == 0 || plan.Stages[i - 1].Stage is not GqlReturnStage)
                { source = ResetBindingAsync(source, layout, budget, token); }
            }
            else { source = StreamStageAsync(stage, source, layout, budget, operation, parameters, token); }
        }
        if (plan.Projections.Count == 0)
        {
            await foreach (var _ in source.ConfigureAwait(false)) { token.ThrowIfCancellationRequested(); }
            return new GraphMutationResult(0, plan.Warnings);
        }
        return await ProjectResultAsync(database, operation, plan, source, parameters, token,
            paths, scalarsOnly, compositeValues, 0).ConfigureAwait(false);
    }

    private static async IAsyncEnumerable<IReadOnlyDictionary<string, object?>> SeedBindingAsync(GraphBindingLayout layout, ExpansionBudget budget,
        [EnumeratorCancellation] CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        var row = new GraphBindingRow(layout);
        using var memory = budget.Memory.Reserve(row.RetainedBytes);
        yield return row;
        await Task.CompletedTask.ConfigureAwait(false);
    }

    private static async IAsyncEnumerable<IReadOnlyDictionary<string, object?>> StreamBindingsAsync(
        IEnumerable<IReadOnlyDictionary<string, object?>> bindings, [EnumeratorCancellation] CancellationToken token)
    {
        foreach (var binding in bindings) { token.ThrowIfCancellationRequested(); yield return binding; }
        await Task.CompletedTask.ConfigureAwait(false);
    }

    private static async IAsyncEnumerable<IReadOnlyDictionary<string, object?>> ResetBindingAsync(
        IAsyncEnumerable<IReadOnlyDictionary<string, object?>> input, GraphBindingLayout layout, ExpansionBudget budget,
        [EnumeratorCancellation] CancellationToken token)
    {
        await foreach (var _ in input.ConfigureAwait(false)) { token.ThrowIfCancellationRequested(); }
        var row = new GraphBindingRow(layout);
        using var memory = budget.Memory.Reserve(row.RetainedBytes);
        yield return row;
    }

    private static async IAsyncEnumerable<IReadOnlyDictionary<string, object?>> StreamStageAsync(GraphStagePlan stage,
        IAsyncEnumerable<IReadOnlyDictionary<string, object?>> input, GraphBindingLayout layout, ExpansionBudget budget,
        GraphOperation operation, IReadOnlyDictionary<string, object?>? parameters, [EnumeratorCancellation] CancellationToken token)
    {
        RuntimeHelpers.EnsureSufficientExecutionStack();
        using var iteratorMemory = budget.Memory.Reserve(256);
        using var rowMemory = new GraphMemoryOwner(budget.Memory);
        await foreach (var binding in input.ConfigureAwait(false))
        {
            token.ThrowIfCancellationRequested();
            operation.EnsureActive();
            switch (stage.Stage)
            {
                case GqlLetStage let:
                    var assigned = new GraphBindingRow(layout, binding);
                    foreach (var assignment in let.Bindings)
                    {
                        token.ThrowIfCancellationRequested();
                        assigned.Add(assignment.Alias!, GraphExpressionEvaluator.Evaluate(assignment.Expression, assigned,
                            operation.EvaluationContext, parameters));
                    }
                    foreach (var assignment in let.Bindings) { rowMemory.RetainValue(assigned[assignment.Alias!], token); }
                    try { using (budget.Memory.Reserve(assigned.RetainedBytes)) { yield return assigned; } }
                    finally { foreach (var assignment in let.Bindings) { rowMemory.ReleaseValue(assigned[assignment.Alias!]); } }
                    break;
                case GqlFilterStage filter:
                    if (GraphExpressionEvaluator.Boolean(GraphExpressionEvaluator.Evaluate(filter.Predicate, binding,
                        operation.EvaluationContext, parameters), filter.Predicate) is true) { yield return binding; }
                    break;
                case GqlForStage loop:
                {
                    object? value = GraphExpressionEvaluator.Evaluate(loop.List, binding, operation.EvaluationContext, parameters);
                    if (value is null) { break; }
                    if (value is not System.Collections.IList list || value is byte[])
                    { throw new GraphPlanningException("CG02013", ErrorMessages.ForListRequired, loop.List.Location); }
                    // The row is borrowed until the consumer's next move. Consumers copy any
                    // retained values, so one slot array serves this complete FOR input list.
                    var row = new GraphBindingRow(layout, binding) { [loop.Variable] = null };
                    using var rowStorage = budget.Memory.Reserve(row.RetainedBytes + 32);
                    bool ownsList = loop.List is not (GqlParameterExpression or GqlVariableExpression);
                    if (ownsList) { rowMemory.RetainValue(list, token); }
                    try
                    {
                    foreach (object? item in list)
                    {
                        token.ThrowIfCancellationRequested();
                        operation.EnsureActive();
                        budget.Consume();
                        if (item is not null && (stage.ElementKind == GraphProjectionKind.Node && item is not GraphNode ||
                            stage.ElementKind == GraphProjectionKind.Relationship && item is not GraphRelationship))
                        { throw new GraphPlanningException("CG02013", ErrorMessages.EntityBindingRequired, loop.List.Location); }
                        row[loop.Variable] = item;
                        bool composite = item is not null && (GraphValueMemory.IsTracked(item) || item is string or byte[] or GraphNode or GraphRelationship);
                        if (composite) { rowMemory.RetainValue(item, token); }
                        try { yield return row; }
                        finally { if (composite) { rowMemory.ReleaseValue(item); } }
                    }
                    }
                    finally { if (ownsList) { rowMemory.ReleaseValue(list); } }
                    break;
                }
                default: throw new UnreachableException();
            }
        }
    }

    private static async IAsyncEnumerable<IReadOnlyDictionary<string, object?>> StreamMatchClauseAsync(GraphDatabase database,
        GraphOperation operation, GraphMatchClausePlan clause, IAsyncEnumerable<IReadOnlyDictionary<string, object?>> input,
        GraphBindingLayout layout, ExpansionBudget budget, IReadOnlyDictionary<string, object?>? parameters,
        [EnumeratorCancellation] CancellationToken token)
    {
        RuntimeHelpers.EnsureSufficientExecutionStack();
        using var iteratorMemory = budget.Memory.Reserve(256);
        await foreach (var binding in input.ConfigureAwait(false))
        {
            token.ThrowIfCancellationRequested();
            operation.EnsureActive();
            bool found = false;
            var outputRow = new GraphBindingRow(layout);
            var parts = new Stack<(int Index, DeferredProperty? Deferred,
                IAsyncEnumerator<PathMatch> Cursor)>();
            try
            {
                if (!clause.MatchesNothing && clause.Paths.Count > 0)
                {
                    parts.Push((0, null, MatchCoreStreamAsync(database, operation, clause.Paths[0],
                        new(binding, StringComparer.Ordinal), budget, parameters, token, deferUnbound: true).GetAsyncEnumerator(token)));
                }
                else if (!clause.MatchesNothing && (clause.Predicate is null || GraphExpressionEvaluator.Boolean(
                    GraphExpressionEvaluator.Evaluate(clause.Predicate, binding, operation.EvaluationContext, parameters), clause.Predicate) is true))
                {
                    found = true;
                    var row = outputRow;
                    row.Reset(binding);
                    using (budget.Memory.Reserve(row.RetainedBytes)) { yield return row; }
                }
                while (parts.Count > 0)
                {
                    token.ThrowIfCancellationRequested();
                    var part = parts.Peek();
                    if (!await part.Cursor.MoveNextAsync().ConfigureAwait(false))
                    { parts.Pop(); await part.Cursor.DisposeAsync().ConfigureAwait(false); continue; }
                    var match = part.Cursor.Current;
                    var deferred = part.Deferred;
                    for (var property = match.Properties; property is not null; property = property.Previous)
                    { deferred = DeferProperties(property.Expected, property.Actual, match.Bindings, deferred); }
                    int next = part.Index + 1;
                    if (next < clause.Paths.Count)
                    {
                        parts.Push((next, deferred, MatchCoreStreamAsync(database, operation, clause.Paths[next],
                            match.Bindings, budget, parameters, token, deferUnbound: true).GetAsyncEnumerator(token)));
                        continue;
                    }
                    var candidate = new MatchCandidate(match.Bindings, deferred);
                    if (!DeferredPropertiesMatch(candidate, operation.EvaluationContext, parameters) ||
                        clause.Predicate is not null && GraphExpressionEvaluator.Boolean(GraphExpressionEvaluator.Evaluate(
                            clause.Predicate, match.Bindings, operation.EvaluationContext, parameters), clause.Predicate) is not true) { continue; }
                    found = true;
                    var row = outputRow;
                    row.Reset(match.Bindings);
                    using (budget.Memory.Reserve(row.RetainedBytes)) { yield return row; }
                }
            }
            finally
            {
                Exception? failure = null;
                while (parts.TryPop(out var part))
                {
                    try { await part.Cursor.DisposeAsync().ConfigureAwait(false); }
                    catch (Exception error) { failure ??= error; }
                }
                if (failure is not null) { ExceptionDispatchInfo.Capture(failure).Throw(); }
            }
            if (!found && clause.Optional)
            {
                var extended = outputRow;
                extended.Reset(binding);
                foreach (string name in clause.NewVariables) { extended.Add(name, null); }
                using (budget.Memory.Reserve(extended.RetainedBytes)) { yield return extended; }
            }
        }
    }

    private static async IAsyncEnumerable<IReadOnlyDictionary<string, object?>> StreamBoundaryAsync(GraphPlan plan,
        GqlReturnStage stage, IAsyncEnumerable<IReadOnlyDictionary<string, object?>> input, GraphBindingLayout layout, ExpansionBudget budget,
        GraphOperation operation, IReadOnlyDictionary<string, object?>? parameters, [EnumeratorCancellation] CancellationToken token)
    {
        RuntimeHelpers.EnsureSufficientExecutionStack();
        using var iteratorMemory = budget.Memory.Reserve(256);
        using var retained = new GraphMemoryOwner(budget.Memory);
        if (plan.SortPage is not { Keys.Count: > 0 })
        {
            if (plan.Grouping is { } grouping)
            { input = StreamGroupsAsync(grouping, input, operation, parameters, token); }
            var distinct = plan.Distinct is null ? null : new HashSet<object?[]>(GraphRowComparer.Instance);
            long offset = plan.SortPage?.Offset ?? 0;
            long? limit = plan.SortPage?.Limit;
            long accepted = 0;
            await using var source = input.GetAsyncEnumerator(token);
            while (limit != 0 && (limit is null || accepted < offset || accepted - offset < limit) &&
                await source.MoveNextAsync().ConfigureAwait(false))
            {
                token.ThrowIfCancellationRequested();
                operation.EnsureActive();
                var binding = source.Current;
                var values = EvaluateProjection(plan, binding, operation, parameters, token);
                if (distinct is not null && !distinct.Add(values)) { continue; }
                if (distinct is not null) { retained.Add(80); retained.RetainValue(values, token); }
                if (accepted++ < offset) { continue; }
                var scope = plan.Grouping is null && plan.Distinct is null ? new GraphBindingRow(layout, binding) : new GraphBindingRow(layout);
                for (int i = 0; i < values.Length; i++)
                { if (plan.Projections[i].BindingName is { } name) { scope[name] = values[i]; } }
                if (stage.Predicate is { } predicate && GraphExpressionEvaluator.Boolean(
                    GraphExpressionEvaluator.Evaluate(predicate, scope, operation.EvaluationContext, parameters), predicate) is not true) { continue; }
                var carried = new GraphBindingRow(layout);
                for (int i = 0; i < values.Length; i++)
                {
                    if (plan.Projections[i].BindingName is not { } name) { continue; }
                    if (!carried.TryAdd(name, values[i]))
                    { throw new GraphPlanningException("CG01015", string.Format(System.Globalization.CultureInfo.CurrentCulture,
                        ErrorMessages.DuplicateResultColumn, name), plan.Projections[i].Expression.Location ?? plan.Query.Location); }
                }
                retained.RetainValue(values, token);
                try { using (budget.Memory.Reserve(carried.RetainedBytes + scope.RetainedBytes)) { yield return carried; } }
                finally { retained.ReleaseValue(values); }
            }
            yield break;
        }
        if (plan.Grouping is { } orderedGrouping)
        { input = StreamGroupsAsync(orderedGrouping, input, operation, parameters, token); }
        using var top = new GraphTopRows(budget.Memory, plan.SortPage, token);
        var seen = plan.Distinct is null ? null : new HashSet<object?[]>(GraphRowComparer.Instance);
        long ordinal = 0;
        await foreach (var binding in input.ConfigureAwait(false))
        {
            operation.EnsureActive();
            var values = EvaluateProjection(plan, binding, operation, parameters, token);
            if (seen is not null && !seen.Add(values)) { continue; }
            if (seen is not null) { retained.Add(80); retained.RetainValue(values, token); }
            GraphBindingRow? scope = null;
            if (stage.Predicate is not null)
            {
                scope = plan.Grouping is null && plan.Distinct is null ? new(layout, binding) : new(layout);
                for (int i = 0; i < values.Length; i++)
                { if (plan.Projections[i].BindingName is { } name) { scope[name] = values[i]; } }
            }
            top.Add(new(values, GraphSortPage.Keys(plan, binding, values, operation.EvaluationContext, token), ordinal++) { Scope = scope });
        }
        var ordered = top.Rows();
        retained.Add(32 + 8L * ordered.Count);
        var paged = GraphSortPage.Apply(ordered, plan.SortPage, token);
        var scopes = stage.Predicate is null ? null : ordered.ToDictionary(row => row.Values, row => row.Scope!);
        foreach (var values in paged)
        {
            token.ThrowIfCancellationRequested();
            if (stage.Predicate is { } predicate && GraphExpressionEvaluator.Boolean(
                GraphExpressionEvaluator.Evaluate(predicate, scopes![values], operation.EvaluationContext, parameters), predicate) is not true) { continue; }
            var row = new GraphBindingRow(layout);
            for (int i = 0; i < values.Length; i++)
            {
                if (plan.Projections[i].BindingName is not { } name) { continue; }
                if (!row.TryAdd(name, values[i]))
                { throw new GraphPlanningException("CG01015", string.Format(System.Globalization.CultureInfo.CurrentCulture,
                    ErrorMessages.DuplicateResultColumn, name), plan.Projections[i].Expression.Location ?? plan.Query.Location); }
            }
            using (budget.Memory.Reserve(row.RetainedBytes)) { yield return row; }
        }
    }

    private static async IAsyncEnumerable<IReadOnlyDictionary<string, object?>> StreamGroupsAsync(GraphGroupingPlan grouping,
        IAsyncEnumerable<IReadOnlyDictionary<string, object?>> input, GraphOperation operation,
        IReadOnlyDictionary<string, object?>? parameters, [EnumeratorCancellation] CancellationToken token)
    {
        using var memory = new GraphMemoryOwner(operation.QueryBudget!.Memory);
        using var accumulator = new GraphGrouping.Accumulator(grouping, operation.EvaluationContext, parameters, token, memory);
        List<Dictionary<string, object?>> groups;
        try
        {
            await foreach (var binding in input.ConfigureAwait(false))
            { operation.EnsureActive(); accumulator.Add(binding); }
            groups = accumulator.Finish();
        }
        catch { accumulator.DisposeAfterFailure(); throw; }
        // Release callback state before publishing materialized result rows.
        accumulator.Dispose();
        foreach (var group in groups) { token.ThrowIfCancellationRequested(); yield return group; }
    }

    private static object?[] EvaluateProjection(GraphPlan plan, IReadOnlyDictionary<string, object?> binding,
        GraphOperation operation, IReadOnlyDictionary<string, object?>? parameters, CancellationToken token)
    {
        var values = new object?[plan.Projections.Count];
        for (int i = 0; i < values.Length; i++)
        {
            token.ThrowIfCancellationRequested();
            values[i] = GraphExpressionEvaluator.Evaluate(plan.Projections[i].Expression, binding, operation.EvaluationContext, parameters);
        }
        return values;
    }

    private static async ValueTask<DatabaseResult> ProjectResultAsync(GraphDatabase database, GraphOperation operation,
        GraphPlan plan, IAsyncEnumerable<IReadOnlyDictionary<string, object?>> input,
        IReadOnlyDictionary<string, object?>? parameters, CancellationToken token, bool paths, bool scalarsOnly,
        bool compositeValues, long affected)
    {
        if (plan.Grouping is { } grouping) { input = StreamGroupsAsync(grouping, input, operation, parameters, token); }
        var compositeColumns = plan.Projections.Select(projection => projection.MayBeComposite).ToArray();
        var budget = operation.QueryBudget ??= new ExpansionBudget();
        using var retained = new GraphMemoryOwner(budget.Memory);
        using var distinctMemory = new GraphMemoryOwner(budget.Memory);
        retained.Add(128L + 64L * plan.Projections.Count);
        var columns = plan.Projections.Select((projection, i) => new DatabaseColumn
        { Name = projection.Name, Ordinal = i, Type = projection.Type, IsNullable = projection.ResultNullable ?? false }).ToArray();
        using var top = new GraphTopRows(budget.Memory, plan.SortPage, token);
        var distinct = plan.Distinct is null ? null : new HashSet<object?[]>(GraphRowComparer.Instance);
        bool valueColumnsAdded = false;
        bool early = !plan.Writes && plan.SortPage is not { Keys.Count: > 0 };
        long offset = early ? plan.SortPage?.Offset ?? 0 : 0;
        long? limit = early ? plan.SortPage?.Limit : null;
        long accepted = 0;
        long ordinal = 0;
        await using (var bindings = input.GetAsyncEnumerator(token))
        {
            while ((!early || limit != 0 && (limit is null || accepted < offset || accepted - offset < limit)) &&
                await bindings.MoveNextAsync().ConfigureAwait(false))
            {
                token.ThrowIfCancellationRequested();
                operation.EnsureActive();
                var binding = bindings.Current;
                var row = EvaluateProjection(plan, binding, operation, parameters, token);
                if (scalarsOnly)
                {
                    for (int i = 0; i < row.Length; i++)
                    {
                        bool unsigned = row[i] is byte or ushort or uint or ulong;
                        if (unsigned && !compositeValues)
                        { throw new GraphPlanningException("CG07016", ErrorMessages.CompositeCapabilityRequired, plan.Query.Location); }
                        if (unsigned || compositeValues && row[i] is decimal)
                        { valueColumnsAdded |= !compositeColumns[i]; compositeColumns[i] = true; }
                    }
                }
                if (!paths) { GraphResultRowEncoder.Validate(row, compositeColumns); }
                if (distinct is not null && !distinct.Add(row)) { continue; }
                if (distinct is not null) { distinctMemory.Add(80); distinctMemory.RetainValue(row, token); }
                if (accepted++ < offset) { continue; }
                var keys = GraphSortPage.Keys(plan, binding, row, operation.EvaluationContext, token);
                top.Add(new GraphOrderedRow(row, keys, ordinal++));
            }
        }
        var rows = top.Rows();
        if (valueColumnsAdded) { foreach (var row in rows) { GraphResultRowEncoder.Validate(row.Values, compositeColumns); } }
        retained.Add(32 + 8L * rows.Count);
        var paged = early ? rows.Select(row => row.Values).ToArray() : GraphSortPage.Apply(rows, plan.SortPage, token);
        if (paths)
        {
            retained.Add(32 + 8L * paged.Length);
            var results = new GraphPath[paged.Length];
            for (int i = 0; i < paged.Length; i++)
            {
                token.ThrowIfCancellationRequested();
                results[i] = paged[i][0] switch
                {
                    GraphPath path => path,
                    GraphNode node => new GraphPath([node], []),
                    GraphRelationship relationship => ProjectRelationship(database, operation, relationship),
                    _ => throw new GraphPlanningException("CG05001", ErrorMessages.PathProjectionRequired, plan.Query.Location),
                };
                retained.RetainValue(results[i], token);
            }
            return new GraphPathsQueryResult(results, plan.Warnings);
        }
        return new GraphQueryResult(columns, paged, plan.Warnings, compositeColumns, plan.Writes ? affected : -1);
    }
}

using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using Assimalign.Cohesion.Database.Execution;
using Assimalign.Cohesion.Database.Graph.Language;
using Assimalign.Cohesion.Database.Graph.Protocol;
using Assimalign.Cohesion.Database.Types;

namespace Assimalign.Cohesion.Database.Graph.Internal;

internal static partial class GraphPlanExecutor
{
    private static void ValidateCompositeResult(GraphCompositePlan composite, bool scalarsOnly, bool compositeValues)
    {
        // Admission visits every operand, even empty ones, before any iterator can execute.
        foreach (var plan in composite.Operands)
        {
            long headerLength = 4;
            foreach (var projection in plan.Projections)
            { headerLength += 5L + System.Text.Encoding.UTF8.GetByteCount(projection.Name); }
            if (headerLength > GraphValueCodec.MaxPayloadLength)
            { throw new GraphPlanningException("CG02013", ErrorMessages.CompositeLimit, plan.Query.Location); }
            if (!compositeValues && plan.Projections.Any(projection => projection.MayBeComposite))
            { throw new GraphPlanningException("CG07016", ErrorMessages.CompositeCapabilityRequired, plan.Query.Location); }
            if (scalarsOnly && plan.Projections.Any(projection => projection.Kind == GraphProjectionKind.EntityValue && projection.MayBeComposite))
            { throw new GraphPlanningException("CG05001", ErrorMessages.EntityListWireUnsupported, plan.Query.Location); }
            if (scalarsOnly && plan.Projections.Any(projection => projection.Kind != GraphProjectionKind.Scalar))
            { throw new GraphPlanningException("CG05001", ErrorMessages.ExecuteScalarRequired, plan.Query.Location); }
        }
    }

    private static async ValueTask<DatabaseResult> ExecuteCompositeAsync(GraphDatabase database, GraphOperation operation,
        GraphCompositePlan composite, CancellationToken token, bool paths, bool scalarsOnly, bool compositeValues)
    {
        ValidateCompositeResult(composite, scalarsOnly, compositeValues);
        var types = composite.Projections.Select(projection => projection.Type).ToArray();
        var dynamicTypes = new bool[types.Length];
        var nullable = new bool[types.Length];
        int columnCount = types.Length;
        var compositeColumns = Enumerable.Range(0, columnCount)
            .Select(i => composite.Operands.Any(plan => plan.Projections[i].MayBeComposite)).ToArray();
        var budget = operation.QueryBudget ??= new ExpansionBudget();
        using var retained = new GraphMemoryOwner(budget.Memory);
        retained.Add(128L + 64L * columnCount);
        var rows = new List<object?[]>();
        await foreach (var row in StreamCompositeRowsAsync(database, operation, composite, token, metadataTypes: types, dynamicTypes: dynamicTypes, nullable: nullable).ConfigureAwait(false))
        {
            if (scalarsOnly)
            {
                for (int i = 0; i < row.Length; i++)
                {
                    bool unsigned = row[i] is byte or ushort or uint or ulong;
                    if (unsigned && !compositeValues)
                    { throw new GraphPlanningException("CG07016", ErrorMessages.CompositeCapabilityRequired, composite.Query.Location); }
                    if (unsigned || compositeValues && row[i] is decimal) { compositeColumns[i] = true; }
                }
            }
            if (!paths) { GraphResultRowEncoder.Validate(row, compositeColumns); }
            retained.Add(16);
            retained.RetainValue(row, token);
            rows.Add(row);
        }
        if (!paths) { foreach (var row in rows) { token.ThrowIfCancellationRequested(); GraphResultRowEncoder.Validate(row, compositeColumns); } }
        retained.Add(32 + 8L * rows.Count);
        if (paths)
        {
            var results = new GraphPath[rows.Count];
            for (int i = 0; i < rows.Count; i++)
            {
                token.ThrowIfCancellationRequested();
                results[i] = rows[i][0] switch
                {
                    GraphPath path => path,
                    GraphNode node => new GraphPath([node], []),
                    GraphRelationship relationship => ProjectRelationship(database, operation, relationship),
                    _ => throw new GraphPlanningException("CG05001", ErrorMessages.PathProjectionRequired, composite.Query.Location),
                };
                retained.RetainValue(results[i], token);
            }
            return new GraphPathsQueryResult(results, composite.Warnings);
        }
        var columns = composite.Projections.Select((projection, i) => new DatabaseColumn
        { Name = projection.Name, Ordinal = i, Type = dynamicTypes[i] ? DatabaseType.Null : types[i],
          IsNullable = nullable[i] || projection.ResultNullable == true }).ToArray();
        return new GraphQueryResult(columns, rows.ToArray(), composite.Warnings, compositeColumns);
    }

    private static async IAsyncEnumerable<object?[]> StreamCompositeRowsAsync(GraphDatabase database, GraphOperation operation,
        GraphCompositePlan composite, [EnumeratorCancellation] CancellationToken token,
        IReadOnlyList<IReadOnlyDictionary<string, object?>>? incoming = null, DatabaseType[]? metadataTypes = null,
        bool[]? dynamicTypes = null, bool[]? nullable = null)
    {
        var budget = operation.QueryBudget!;
        using var distinctMemory = new GraphMemoryOwner(budget.Memory);
        var kinds = (GraphFunctionKind[])composite.ColumnKinds.Clone();
        var seen = composite.Operator == GqlCompositeOperator.Union ? new HashSet<object?[]>(GraphRowComparer.Instance) : null;
        var parentBindings = operation.FunctionBindings;
        try
        {
            foreach (var plan in composite.Operands)
            {
                await using var source = StreamCompositeOperandAsync(database, operation, plan, token, incoming).GetAsyncEnumerator(token);
                while (true)
                {
                    token.ThrowIfCancellationRequested(); operation.EnsureActive();
                    operation.FunctionBindings = plan.FunctionBindings;
                    operation.FunctionCancellation = token;
                    if (!await source.MoveNextAsync().ConfigureAwait(false)) { break; }
                    var row = source.Current;
                    budget.Consume();
                    for (int i = 0; i < row.Length; i++)
                    {
                        var kind = GraphScalarFunctions.Kind(row[i]);
                        if ((kind & GraphFunctionKind.Number) != 0) { kind |= GraphFunctionKind.Number; }
                        if (kind == GraphFunctionKind.Unknown) { continue; }
                        if (kinds[i] != GraphFunctionKind.Unknown && (kinds[i] & kind) == 0)
                        { throw new GraphPlanningException("CG02017", ErrorMessages.CompositeShapes, plan.Query.Location); }
                        kinds[i] = kinds[i] == GraphFunctionKind.Unknown ? kind : kinds[i] & kind;
                    }
                    // Reconcile every operand value before duplicate elimination keeps its first representative.
                    if (metadataTypes is not null)
                    {
                        for (int i = 0; i < row.Length; i++)
                        {
                            if (row[i] is null) { nullable![i] = true; continue; }
                            var type = GraphCompositeMetadata.Type(row[i]);
                            if (type == DatabaseType.Null) { dynamicTypes![i] = true; }
                            var widened = GraphCompositeMetadata.Widen(metadataTypes[i], type);
                            if (metadataTypes[i] != DatabaseType.Null && type != DatabaseType.Null && widened == DatabaseType.Null) { dynamicTypes![i] = true; }
                            metadataTypes[i] = widened;
                        }
                    }
                    if (seen is not null)
                    {
                        if (!seen.Add(row)) { continue; }
                        try { distinctMemory.Add(80); distinctMemory.RetainValue(row, token); }
                        catch (GraphPlanningException error) when (error.Diagnostics[0].Code == "CG05020")
                        { throw new GraphPlanningException("CG05030", ErrorMessages.UnionMemoryExceeded, composite.Query.Location); }
                    }
                    operation.FunctionBindings = parentBindings;
                    yield return row;
                }
            }
        }
        finally { operation.FunctionBindings = parentBindings; }
    }

    private static async IAsyncEnumerable<IReadOnlyDictionary<string, object?>> StreamCompositeBindingsAsync(GraphDatabase database,
        GraphOperation operation, GraphCompositePlan composite, IAsyncEnumerable<IReadOnlyDictionary<string, object?>> source,
        [EnumeratorCancellation] CancellationToken token)
    {
        var budget = operation.QueryBudget!;
        using var memory = new GraphMemoryOwner(budget.Memory);
        using var rowMemory = new GraphMemoryOwner(budget.Memory);
        var incoming = new List<IReadOnlyDictionary<string, object?>>();
        await foreach (var binding in source.ConfigureAwait(false))
        {
            token.ThrowIfCancellationRequested(); operation.EnsureActive();
            var copy = new Dictionary<string, object?>(binding, StringComparer.Ordinal);
            memory.Add(64); memory.RetainValue(copy, token); incoming.Add(copy);
        }
        await foreach (var values in StreamCompositeRowsAsync(database, operation, composite, token, incoming).ConfigureAwait(false))
        {
            var binding = new Dictionary<string, object?>(StringComparer.Ordinal);
            for (int i = 0; i < values.Length; i++) { binding.Add(composite.Projections[i].Name, values[i]); }
            rowMemory.RetainValue(binding, token);
            try { yield return binding; }
            finally { rowMemory.ReleaseValue(binding); }
        }
    }

    // Only composite queries use this cursor. Ordinary read allocation and dispatch stay intact.
    private static async IAsyncEnumerable<object?[]> StreamCompositeOperandAsync(GraphDatabase database, GraphOperation operation,
        GraphPlan plan, [EnumeratorCancellation] CancellationToken token,
        IReadOnlyList<IReadOnlyDictionary<string, object?>>? incoming = null)
    {
        var budget = operation.QueryBudget!;
        using var executionMemory = budget.Memory.Reserve(512);
        using var layoutMemory = new GraphMemoryOwner(budget.Memory);
        using var distinctMemory = new GraphMemoryOwner(budget.Memory);
        var layout = new GraphBindingLayout(layoutMemory);
        var parameters = plan.Parameters;
        IAsyncEnumerable<IReadOnlyDictionary<string, object?>> input = incoming is null ? SeedBindingAsync(layout, budget, token) : StreamBindingsAsync(incoming, token);
        for (int i = 0; i < plan.Stages.Count - 1; i++)
        {
            token.ThrowIfCancellationRequested();
            var stage = plan.Stages[i];
            if (stage is GraphCompositeStagePlan nested)
            { input = StreamCompositeBindingsAsync(database, operation, nested.Composite, input, token); }
            else if (stage.Stage is GqlMatchStage)
            {
                foreach (var clause in stage.Plan.MatchClauses)
                { input = StreamMatchClauseAsync(database, operation, clause, input, layout, budget, parameters, token); }
            }
            else if (stage.Stage is GqlReturnStage projection)
            { input = StreamBoundaryAsync(stage.Plan, projection, input, layout, budget, operation, parameters, token); }
            else if (stage.Stage is not GqlNextStage)
            { input = StreamStageAsync(stage, input, layout, budget, operation, parameters, token); }
        }
        if (plan.Stages[^1] is GraphCompositeStagePlan finalComposite)
        { input = StreamCompositeBindingsAsync(database, operation, finalComposite.Composite, input, token); }
        if (plan.Grouping is { } grouping) { input = StreamGroupsAsync(grouping, input, operation, parameters, token); }
        var distinct = plan.Distinct is null ? null : new HashSet<object?[]>(GraphRowComparer.Instance);
        bool ordered = plan.SortPage is { Keys.Count: > 0 };
        using var top = ordered ? new GraphTopRows(budget.Memory, plan.SortPage, token) : null;
        long offset = ordered ? 0 : plan.SortPage?.Offset ?? 0;
        long? limit = ordered ? null : plan.SortPage?.Limit;
        long accepted = 0;
        long ordinal = 0;
        await using (var source = input.GetAsyncEnumerator(token))
        {
            while ((limit is null || accepted < offset || accepted - offset < limit) && limit != 0 &&
                await source.MoveNextAsync().ConfigureAwait(false))
            {
                token.ThrowIfCancellationRequested();
                operation.EnsureActive();
                var row = EvaluateProjection(plan, source.Current, operation, parameters, token);
                if (distinct is not null)
                {
                    if (!distinct.Add(row)) { continue; }
                    distinctMemory.Add(80); distinctMemory.RetainValue(row, token);
                }
                if (accepted++ < offset) { continue; }
                if (ordered) { top!.Add(new(row, GraphSortPage.Keys(plan, source.Current, row, operation.EvaluationContext, token), ordinal++)); }
                else { yield return row; }
            }
        }
        if (ordered)
        {
            var orderedRows = top!.Rows();
            using var pageMemory = budget.Memory.Reserve(32 + 8L * orderedRows.Count);
            var rows = GraphSortPage.Apply(orderedRows, plan.SortPage, token);
            foreach (var row in rows) { token.ThrowIfCancellationRequested(); yield return row; }
        }
    }
}

using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using Assimalign.Cohesion.Database.Graph.Language;
using Assimalign.Cohesion.Database.Types;

namespace Assimalign.Cohesion.Database.Graph.Internal;

internal static partial class GraphPlanExecutor
{
    private static async IAsyncEnumerable<object?[]> StreamCompositeTreeRowsAsync(GraphDatabase database,
        GraphOperation operation, GraphCompositePlan composite, [EnumeratorCancellation] CancellationToken token,
        IReadOnlyList<IReadOnlyDictionary<string, object?>>? incoming, DatabaseType[]? metadataTypes,
        bool[]? dynamicTypes, bool[]? nullable)
    {
        var budget = operation.QueryBudget!;
        var stack = new List<GraphCompositeCursor>();
        var kinds = (GraphFunctionKind[])composite.ColumnKinds.Clone();
        var parentBindings = operation.FunctionBindings;
        void Push(int index)
        {
            var cursor = new GraphCompositeCursor(composite.Nodes[index], budget.Memory);
            stack.Add(cursor);
            RetainCompositeState(cursor, null, 320, composite, token); // frame, stack slot, owner and empty hash/list headers
        }
        try
        {
            Push(0);
            while (stack.Count != 0)
            {
                token.ThrowIfCancellationRequested(); operation.EnsureActive();
                var cursor = stack[^1];
                object?[]? row = null;
                bool complete = false;
                if (cursor.Node.Operator is null)
                {
                    var plan = composite.Operands[cursor.Node.Operand];
                    cursor.Source ??= StreamCompositeOperandAsync(database, operation, plan, token, incoming).GetAsyncEnumerator(token);
                    operation.FunctionBindings = plan.FunctionBindings;
                    operation.FunctionCancellation = token;
                    if (await cursor.Source.MoveNextAsync().ConfigureAwait(false))
                    {
                        row = cursor.Source.Current;
                        budget.Consume();
                        ReconcileCompositeRow(row, plan, kinds, metadataTypes, dynamicTypes, nullable);
                    }
                    else { complete = true; }
                }
                else if (cursor.Phase == GraphCompositeCursorPhase.BeginLeft)
                { cursor.Phase = GraphCompositeCursorPhase.ReadLeft; Push(cursor.Node.Left); continue; }
                else if (cursor.Phase == GraphCompositeCursorPhase.BeginRight)
                { cursor.Phase = GraphCompositeCursorPhase.ReadRight; Push(cursor.Node.Right); continue; }
                else if (cursor.Phase == GraphCompositeCursorPhase.Emit && cursor.Rows is { } rows)
                {
                    while (cursor.OutputIndex < rows.Count)
                    {
                        token.ThrowIfCancellationRequested(); operation.EnsureActive(); budget.Consume();
                        var candidate = rows[cursor.OutputIndex++];
                        var count = cursor.Counts![candidate];
                        bool intersect = cursor.Node.Operator is GqlCompositeOperator.Intersect or GqlCompositeOperator.IntersectAll;
                        if ((intersect ? count.Matched : count.Available) == 0) { continue; }
                        cursor.Counts[candidate] = intersect ? count with { Matched = count.Matched - 1 } : count with { Available = count.Available - 1 };
                        row = candidate;
                        break;
                    }
                    complete = row is null;
                }
                else { complete = true; }

                if (complete)
                {
                    try { if (cursor.Source is { } source) { await source.DisposeAsync().ConfigureAwait(false); cursor.Source = null; } }
                    finally { cursor.Memory.Dispose(); }
                    stack.RemoveAt(stack.Count - 1);
                    if (stack.Count != 0)
                    {
                        var parent = stack[^1];
                        parent.Phase = parent.Phase == GraphCompositeCursorPhase.ReadLeft
                            ? parent.Node.Operator == GqlCompositeOperator.Otherwise && parent.HasRows ? GraphCompositeCursorPhase.Complete : GraphCompositeCursorPhase.BeginRight
                            : GraphCompositeCursorPhase.Emit;
                    }
                    continue;
                }
                // Deliver this row upward until an operator consumes it, or the root emits it.
                for (int level = stack.Count - 2; level >= 0 && row is not null; level--)
                {
                    token.ThrowIfCancellationRequested(); operation.EnsureActive();
                    var parent = stack[level];
                    budget.Consume(); // Every parent hashes or forwards this row, including duplicate hash visits.
                    if (parent.Node.Operator == GqlCompositeOperator.Otherwise) { parent.HasRows = true; }
                    else if (parent.Seen is { } seen)
                    {
                        if (!seen.Add(row)) { row = null; continue; }
                        RetainCompositeState(parent, row, 80, composite, token);
                    }
                    else if (parent.Counts is { } counts)
                    {
                        bool all = parent.Node.Operator is GqlCompositeOperator.ExceptAll or GqlCompositeOperator.IntersectAll;
                        if (parent.Phase == GraphCompositeCursorPhase.ReadLeft)
                        {
                            bool known = counts.TryGetValue(row, out var count);
                            if (!known || all)
                            {
                                RetainCompositeState(parent, row, known ? 16 : 112, composite, token);
                                parent.Rows!.Add(row);
                                counts[row] = (count.Available + 1, 0);
                            }
                        }
                        else if (counts.TryGetValue(row, out var count) && count.Available > 0)
                        { counts[row] = (count.Available - 1, count.Matched + 1); }
                        row = null;
                    }
                }
                if (row is not null)
                {
                    operation.FunctionBindings = parentBindings;
                    yield return row;
                }
            }
        }
        finally
        {
            operation.FunctionBindings = parentBindings;
            // Only the active leaf owns a source; every frame owns a separate memory lease.
            try
            {
                for (int i = stack.Count - 1; i >= 0; i--)
                { if (stack[i].Source is { } source) { await source.DisposeAsync().ConfigureAwait(false); } }
            }
            finally { foreach (var cursor in stack) { cursor.Memory.Dispose(); } }
        }
    }

    private static void RetainCompositeState(GraphCompositeCursor cursor, object?[]? row, long bytes,
        GraphCompositePlan composite, CancellationToken token)
    {
        try { cursor.Memory.Add(bytes); cursor.Memory.RetainValue(row, token); }
        catch (GraphPlanningException error) when (error.Diagnostics[0].Code == "CG05020" &&
            (cursor.Seen is not null || cursor.Counts is not null))
        {
            bool union = cursor.Node.Operator == GqlCompositeOperator.Union;
            throw new GraphPlanningException(union ? "CG05030" : "CG05032",
                union ? ErrorMessages.UnionMemoryExceeded : ErrorMessages.SetStateMemoryExceeded, composite.Query.Location);
        }
    }

    private static void ReconcileCompositeRow(object?[] row, GraphPlan plan, GraphFunctionKind[] kinds,
        DatabaseType[]? metadataTypes, bool[]? dynamicTypes, bool[]? nullable)
    {
        for (int i = 0; i < row.Length; i++)
        {
            var kind = GraphScalarFunctions.Kind(row[i]);
            if ((kind & GraphFunctionKind.Number) != 0) { kind |= GraphFunctionKind.Number; }
            if (kind != GraphFunctionKind.Unknown)
            {
                if (kinds[i] != GraphFunctionKind.Unknown && (kinds[i] & kind) == 0)
                { throw new GraphPlanningException("CG02017", ErrorMessages.CompositeShapes, plan.Query.Location); }
                kinds[i] = kinds[i] == GraphFunctionKind.Unknown ? kind : kinds[i] & kind;
            }
            if (metadataTypes is null) { continue; }
            if (row[i] is null) { nullable![i] = true; continue; }
            var type = GraphCompositeMetadata.Type(row[i]);
            if (type == DatabaseType.Null) { dynamicTypes![i] = true; }
            var widened = GraphCompositeMetadata.Widen(metadataTypes[i], type);
            if (metadataTypes[i] != DatabaseType.Null && type != DatabaseType.Null && widened == DatabaseType.Null) { dynamicTypes![i] = true; }
            metadataTypes[i] = widened;
        }
    }
}

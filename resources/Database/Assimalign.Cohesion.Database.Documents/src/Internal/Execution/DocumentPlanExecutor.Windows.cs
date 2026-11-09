using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

using Assimalign.Cohesion.Database.Documents.Language;

namespace Assimalign.Cohesion.Database.Documents.Internal;

internal static partial class DocumentPlanExecutor
{
    // An execution-only binding, outside the language identifier grammar. It never renders.
    internal const string WindowBindingName = "\0OQL_WINDOW";

    private static async ValueTask ApplyWindowsAsync(OqlSelectExpression query, List<EvaluatedRow> rows,
        DocumentExpressionEvaluator evaluator, DocumentNestedExecutionContext nested,
        DocumentAggregateMemory.Reservation retained, CancellationToken cancellationToken)
    {
        var memory = evaluator.Memory;
        var definitions = DocumentPlanner.WindowDefinitions(query);
        var functions = DocumentPlanner.Windows(query).Distinct().ToArray();
        retained.Charge(512L + functions.Length * 256L + rows.Count * (192L + functions.Length * 96L));
        var results = new Dictionary<OqlWindowExpression, object?>[rows.Count];
        for (int i = 0; i < rows.Count; i++) { results[i] = new Dictionary<OqlWindowExpression, object?>(functions.Length); }
        var pending = functions.ToList();
        while (pending.Count > 0)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var specification = DocumentPlanner.ResolveWindow(pending[0], definitions);
            var compatible = pending.Where(w => DocumentPlanner.EquivalentWindowSpecifications(specification,
                DocumentPlanner.ResolveWindow(w, definitions), query)).ToArray();
            foreach (var function in compatible) { pending.Remove(function); }
            var prepared = compatible.Select(function =>
            {
                var kind = OqlFunctions.Find(function.Call.Name)!.WindowKind;
                long buckets = kind == OqlWindowFunctionKind.Ntile ? DocumentPlanner.WindowInteger(function.Call.Arguments[0], true) : 0;
                long offset = kind is OqlWindowFunctionKind.Lag or OqlWindowFunctionKind.Lead && function.Call.Arguments.Count > 1
                    ? DocumentPlanner.WindowInteger(function.Call.Arguments[1], false) : 1;
                return (Function: function, Kind: kind, Buckets: buckets, Offset: offset);
            }).ToArray();
            using var sortMemory = memory.Reserve();
            sortMemory.Charge(512L + rows.Count * (128L + 16L * (specification.PartitionBy.Count + specification.OrderBy.Count)));
            var partitions = new object?[rows.Count][];
            var ordering = new object?[rows.Count][];
            var indexes = new int[rows.Count];
            for (int i = 0; i < rows.Count; i++)
            {
                indexes[i] = i;
                partitions[i] = new object?[specification.PartitionBy.Count];
                ordering[i] = new object?[specification.OrderBy.Count];
                for (int j = 0; j < partitions[i].Length; j++)
                { partitions[i][j] = await EvaluateAsync(specification.PartitionBy[j], rows[i], sortMemory).ConfigureAwait(false); }
                for (int j = 0; j < ordering[i].Length; j++)
                { ordering[i][j] = await EvaluateAsync(specification.OrderBy[j].Expression, rows[i], sortMemory).ConfigureAwait(false); }
            }
            // Array.Sort wraps comparer exceptions. Restore the original coded budget/cancel failure.
            try
            {
                if (specification.PartitionBy.Count > 0 || specification.OrderBy.Count > 0)
                {
                    Array.Sort(indexes, (a, b) =>
                    {
                        nested.ConsumeWork(cancellationToken);
                        int comparison = TupleComparer.Instance.Compare(partitions[a], partitions[b]);
                        if (comparison != 0) { return comparison; }
                        comparison = ComparePeers(a, b);
                        return comparison != 0 ? comparison : a.CompareTo(b);
                    });
                }
            }
            catch (InvalidOperationException error) when (error.InnerException is DatabaseException or ObjectDisposedException or OperationCanceledException)
            { System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(error.InnerException).Throw(); throw; }

            int start = 0;
            while (start < indexes.Length)
            {
                int end = start + 1;
                while (end < indexes.Length && TupleComparer.Instance.Compare(partitions[indexes[start]], partitions[indexes[end]]) == 0)
                { nested.ConsumeWork(cancellationToken); end++; }
                int count = end - start;
                foreach (var (function, kind, buckets, offset) in prepared)
                {
                    long dense = 0;
                    int peerStart = 0;
                    while (peerStart < count)
                    {
                        dense++;
                        int peerEnd = peerStart + 1;
                        while (peerEnd < count && ComparePeers(indexes[start + peerStart], indexes[start + peerEnd]) == 0)
                        { nested.ConsumeWork(cancellationToken); peerEnd++; }
                        for (int position = peerStart; position < peerEnd; position++)
                        {
                            nested.ConsumeWork(cancellationToken);
                            int ordinal = indexes[start + position];
                            object? value = kind switch
                            {
                                OqlWindowFunctionKind.RowNumber => (long)position + 1,
                                OqlWindowFunctionKind.Rank => (long)peerStart + 1,
                                OqlWindowFunctionKind.DenseRank => dense,
                                OqlWindowFunctionKind.PercentRank => count == 1 ? 0m : (decimal)peerStart / (count - 1),
                                OqlWindowFunctionKind.CumeDist => (decimal)peerEnd / count,
                                OqlWindowFunctionKind.Ntile => Bucket(position, count, buckets),
                                _ => null,
                            };
                            if (kind is OqlWindowFunctionKind.Lag or OqlWindowFunctionKind.Lead)
                            {
                                // Compare before addition/subtraction so every Int64 offset is safe.
                                bool inside = kind == OqlWindowFunctionKind.Lag ? offset <= position : offset < count - position;
                                if (inside)
                                {
                                    int target = (int)(kind == OqlWindowFunctionKind.Lag ? position - offset : position + offset);
                                    value = await EvaluateAsync(function.Call.Arguments[0], rows[indexes[start + target]], retained).ConfigureAwait(false);
                                }
                                else if (function.Call.Arguments.Count == 3)
                                { value = await EvaluateAsync(function.Call.Arguments[2], rows[ordinal], retained).ConfigureAwait(false); }
                                else { retained.Retain(value); }
                            }
                            else { retained.Retain(value); }
                            results[ordinal].Add(function, value);
                        }
                        peerStart = peerEnd;
                    }
                }
                start = end;
            }

            int ComparePeers(int a, int b)
            {
                for (int j = 0; j < specification.OrderBy.Count; j++)
                {
                    int comparison = DocumentExpressionEvaluator.CompareOrdered(ordering[a][j], ordering[b][j], specification.OrderBy[j]);
                    if (comparison != 0) { return comparison; }
                }
                return 0;
            }
        }
        for (int i = 0; i < rows.Count; i++)
        { rows[i] = rows[i] with { Document = rows[i].Document.Bind(WindowBindingName, results[i]) }; }

        async ValueTask<object?> EvaluateAsync(OqlExpression expression, EvaluatedRow row, DocumentAggregateMemory.Reservation owner)
        {
            long before = memory.Bytes - nested.RetainedBytes;
            object? value;
            long ownerStart = owner.Bytes;
            try
            {
                value = await evaluator.EvaluateAsync(expression, row.Document, row.Group, nested).ConfigureAwait(false);
                // Own subquery and constructor values before another evaluation can evict them.
                owner.Retain(value);
                if (value is JsonElement json) { value = json.Clone(); }
            }
            finally
            {
                memory.Release(memory.Bytes - nested.RetainedBytes - before - (owner.Bytes - ownerStart));
            }
            return value;
        }
    }

    private static long Bucket(int position, int count, long buckets)
    {
        if (buckets >= count) { return (long)position + 1; }
        long size = count / buckets;
        long larger = count % buckets;
        long boundary = (size + 1) * larger;
        return position < boundary ? position / (size + 1) + 1 : larger + (position - boundary) / size + 1;
    }
}

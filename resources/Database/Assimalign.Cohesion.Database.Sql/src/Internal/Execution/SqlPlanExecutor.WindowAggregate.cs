using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.ExceptionServices;
using System.Threading;
using System.Threading.Tasks;

using Assimalign.Cohesion.Database.Execution;
using Assimalign.Cohesion.Database.Sql.Language;
using Assimalign.Cohesion.Database.Types;

namespace Assimalign.Cohesion.Database.Sql.Internal;

internal sealed partial class SqlPlanExecutor
{
    private async Task<DatabaseResult> ExecuteWindowAggregateAsync(SqlWindowAggregatePlan plan, SqlStatementContext statement,
        CancellationToken cancellationToken)
    {
        using var memory = new SqlWindowMemory(statement.Metrics, _definitions.StatementMemoryBudget);
        await using var input = (SqlMaterializedResultSet)await ExecuteAsync(plan.Input, statement, cancellationToken).ConfigureAwait(false);
        var evaluator = ExecutionEvaluator(_subqueryValues, cancellationToken);
        var rows = new List<object?[]>();
        int width = plan.InputWidth + plan.Groups.Sum(group => group.Functions.Count);
        foreach (var source in input.Rows)
        {
            cancellationToken.ThrowIfCancellationRequested();
            memory.Reserve(96L + width * 8L);
            var row = new object?[width];
            Array.Copy(source, row, source.Length);
            rows.Add(row);
        }
        memory.Reserve(128L + 8L * rows.Count); // sort index plus growth/sort scratch
        var sorted = Enumerable.Range(0, rows.Count).ToArray();
        object?[][]? keys = null;
        Collation[]? collations = null;
        long keyBytes = 0;
        var frameOffsets = new Dictionary<SqlWindowFrameBinding, (long Start, long End)>();
        foreach (var group in plan.Groups)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (statement.Metrics.Analysis(group) is { } work) { work.Candidates += rows.Count; }
            var sortWork = statement.Metrics.Analysis(group.Functions);
            if (!group.SortShared)
            {
                if (keyBytes > 0) { keys = null; collations = null; memory.Release(keyBytes); keyBytes = 0; }
                ReserveKeys(128L + (group.Partition.Count + group.Ordering.Count) * 64L + 8L * rows.Count);
                var ordering = group.Partition.Concat(group.Ordering).ToArray();
                keys = new object?[rows.Count][];
                collations = new Collation[ordering.Length];
                for (int i = 0; i < rows.Count; i++)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    ReserveKeys(24L + 8L * ordering.Length);
                    keys[i] = new object?[ordering.Length];
                    for (int k = 0; k < ordering.Length; k++)
                    {
                        await PrepareSubqueriesAsync(ordering[k].Key, rows[i], statement, cancellationToken).ConfigureAwait(false);
                        var value = evaluator.Evaluate(ordering[k].Key, rows[i]);
                        if (value is not null) { ReserveKeys(SqlHashJoinTable.ValueBytes(value)); }
                        keys[i][k] = value;
                        collations[k] = ordering[k].Collation.Resolve(_subqueryValues);
                    }
                }
                int comparisons = 0;
                try
                {
                    Array.Sort(sorted, (a, b) =>
                    {
                        if ((comparisons++ & 63) == 0) { cancellationToken.ThrowIfCancellationRequested(); }
                        for (int k = 0; k < ordering.Length; k++)
                        {
                            int comparison = CompareWindowKey(keys[a][k], keys[b][k], ordering[k], collations[k]);
                            if (comparison != 0) { return comparison; }
                        }
                        return a.CompareTo(b);
                    });
                }
                catch (InvalidOperationException failure) when (failure.InnerException is DatabaseException or OperationCanceledException)
                { ExceptionDispatchInfo.Throw(failure.InnerException); throw; }
                if (sortWork is not null) { sortWork.Candidates++; }
            }
            if (rows.Count > 0)
            {
                foreach (var function in group.Functions)
                {
                    if (function.Frame is { } frame && !frameOffsets.ContainsKey(frame))
                    { frameOffsets.Add(frame, (await FrameOffsetAsync(frame.StartOffset).ConfigureAwait(false), await FrameOffsetAsync(frame.EndOffset).ConfigureAwait(false))); }
                }
            }
            int partitionStart = 0;
            while (partitionStart < sorted.Length)
            {
                cancellationToken.ThrowIfCancellationRequested();
                int partitionEnd = partitionStart + 1;
                while (partitionEnd < sorted.Length && SameKeys(partitionStart, partitionEnd, group.Partition.Count)) { partitionEnd++; }
                // Partition scratch is admitted and released at the partition boundary. Output
                // cells remain in the separately charged input/result rows until the stage ends.
                long scratch = 256L + group.Functions.Count * 128L;
                memory.Reserve(scratch);
                try { await ComputePartitionAsync(partitionStart, partitionEnd, group).ConfigureAwait(false); }
                finally { memory.Release(scratch); }
                partitionStart = partitionEnd;
            }
        }
        return _subqueryValues is { HasCorrelatedQueries: true }
            ? await ExecuteCorrelatedSelectAsync(rows, plan.Projections, null, plan.OrderBy, plan.Limit, plan.Offset,
                plan.IsDistinct, statement, cancellationToken).ConfigureAwait(false)
            : MaterializeSelect(rows, plan.Projections, plan.OrderBy, plan.Limit, plan.Offset, plan.IsDistinct,
                evaluator, plan.OrderByProjections, cancellationToken);

        void ReserveKeys(long bytes) { memory.Reserve(bytes); keyBytes += bytes; }

        bool SameKeys(int a, int b, int length)
        {
            cancellationToken.ThrowIfCancellationRequested();
            for (int k = 0; k < length; k++)
            {
                var left = keys![sorted[a]][k]; var right = keys[sorted[b]][k];
                if (left is null ? right is not null : right is null || SqlValueComparer.Compare(left, right, collations![k]) != 0) { return false; }
            }
            return true;
        }

        async Task ComputePartitionAsync(int start, int end, SqlWindowGroup group)
        {
            int size = end - start;
            var buckets = new long?[group.Functions.Count];
            var accumulators = new SqlAggregateAccumulator?[group.Functions.Count];
            var nextInputs = new int[group.Functions.Count];
            var previousStarts = Enumerable.Repeat(-1, group.Functions.Count).ToArray();
            var previousEnds = Enumerable.Repeat(-1, group.Functions.Count).ToArray();
            var results = new object?[group.Functions.Count];
            Array.Fill(nextInputs, start);
            for (int f = 0; f < group.Functions.Count; f++)
            {
                var function = group.Functions[f];
                if (function.Name != "NTILE") { continue; }
                await PrepareSubqueriesAsync(function.Arguments[0], rows[sorted[start]], statement, cancellationToken).ConfigureAwait(false);
                var value = evaluator.Evaluate(function.Arguments[0], rows[sorted[start]]);
                if (value is null) { continue; }
                buckets[f] = WindowInteger(value, function);
                if (buckets[f] <= 0) { throw new SqlExecutionException("CS05078", ErrorMessages.WindowBucketCount, location: function.Expression.Location); }
            }
            long dense = 0;
            int peerStart = start;
            while (peerStart < end)
            {
                int peerEnd = peerStart + 1;
                while (peerEnd < end && SameKeys(peerStart, peerEnd, group.Partition.Count + group.Ordering.Count)) { peerEnd++; }
                dense++;
                for (int position = peerStart; position < peerEnd; position++)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    var row = rows[sorted[position]];
                    for (int f = 0; f < group.Functions.Count; f++)
                    {
                        var function = group.Functions[f];
                        var (frameStart, frameEnd) = FrameBounds(function, position, start, end, peerStart, peerEnd);
                        object? result = function.Aggregate is not null
                            ? await AggregateFrameAsync(function, f, frameStart, frameEnd).ConfigureAwait(false)
                            : function.Name is "FIRST_VALUE" or "LAST_VALUE" or "NTH_VALUE"
                                ? await ValueFrameAsync(function, position, frameStart, frameEnd).ConfigureAwait(false)
                                : function.Name switch
                        {
                            "ROW_NUMBER" => (long)(position - start + 1),
                            "RANK" => (long)(peerStart - start + 1),
                            "DENSE_RANK" => dense,
                            "PERCENT_RANK" => size == 1 ? 0d : (double)(peerStart - start) / (size - 1),
                            "CUME_DIST" => (double)(peerEnd - start) / size,
                            "NTILE" => buckets[f] is { } count ? Tile(position - start, size, count) : null,
                            _ => await NavigateAsync(function, position, start, end).ConfigureAwait(false),
                        };
                        if (result is not null) { memory.Reserve(SqlHashJoinTable.ValueBytes(result)); }
                        row[function.Ordinal] = result;
                    }
                }
                peerStart = peerEnd;
            }
            for (int i = 0; i < accumulators.Length; i++) { if (accumulators[i] is not null) { memory.Release(256); } }

            async Task<object?> AggregateFrameAsync(SqlWindowFunction function, int index, int first, int last)
            {
                if (previousStarts[index] == first && previousEnds[index] == last) { return results[index]; }
                var aggregate = function.Aggregate!;
                var collation = aggregate.Collation.Resolve(_subqueryValues);
                bool orderedInput = aggregate.OrderBy.Count > 0 && !SqlStandardLibrary.IgnoresWindowOrdering(aggregate.Function);
                bool incremental = !orderedInput && SqlStandardLibrary.Contains(aggregate.Function);
                bool recomputing = !incremental || previousStarts[index] >= 0 && previousStarts[index] != first;
                SqlAggregateAccumulator accumulator;
                if (incremental)
                {
                    // Keep transitions while the frame head stays put, including a bounded
                    // PRECEDING head clamped to the partition start. Without an inverse
                    // transition a moved head requires a fresh aggregate over the new frame.
                    if (accumulators[index] is not null && previousStarts[index] != first)
                    { accumulators[index] = null; memory.Release(256); }
                    if (accumulators[index] is null) { nextInputs[index] = first; }
                    accumulator = accumulators[index] ??= Create();
                }
                else { accumulator = Create(); }
                long retained = 0;
                try
                {
                    var ordered = orderedInput ? new GroupAggregateState(accumulator, aggregate, _subqueryValues) : null;
                    for (int inputPosition = incremental ? nextInputs[index] : first; inputPosition < last; inputPosition++)
                    {
                        cancellationToken.ThrowIfCancellationRequested();
                        if (recomputing) { statement.Metrics.RecordWindowFrameRecomputation(); }
                        if (statement.Metrics.Analysis(function) is { } work) { work.Candidates++; }
                        var inputRow = rows[sorted[inputPosition]];
                        await PrepareSubqueriesAsync(aggregate.Filter, inputRow, statement, cancellationToken).ConfigureAwait(false);
                        if (!evaluator.Matches(aggregate.Filter, inputRow)) { continue; }
                        foreach (var argument in aggregate.Arguments)
                        { await PrepareSubqueriesAsync(argument, inputRow, statement, cancellationToken).ConfigureAwait(false); }
                        if (orderedInput)
                        {
                            foreach (var order in aggregate.OrderBy)
                            { await PrepareSubqueriesAsync(order.Key, inputRow, statement, cancellationToken).ConfigureAwait(false); }
                        }
                        if (ordered is null) { AddRow(accumulator, aggregate, collation, evaluator, inputRow, cancellationToken, statement.TransactionStart); }
                        else
                        {
                            retained += AddAggregateInput(ordered, aggregate, collation, evaluator, inputRow,
                                cancellationToken, statement.TransactionStart, memory);
                        }
                    }
                    if (ordered?.Inputs is { } inputs)
                    {
                        foreach (var inputRow in SortRows(inputs, ordered.Ordering, evaluator, cancellationToken))
                        { AddAggregateValues(accumulator, aggregate, collation, inputRow, cancellationToken, statement.TransactionStart); }
                    }
                    nextInputs[index] = Math.Max(first, last);
                    previousStarts[index] = first;
                    previousEnds[index] = last;
                    return results[index] = NormalizeGroupValue((incremental ? accumulator.ReadWindowResult(cancellationToken) : accumulator.Finish(cancellationToken)).ToObject(), function.Type);
                }
                finally
                {
                    memory.Release(retained);
                    if (!incremental) { memory.Release(256); }
                }

                SqlAggregateAccumulator Create()
                {
                    memory.Reserve(256);
                    return aggregate.Function.CreateAccumulator(new SqlFunctionContext(aggregate.Database, collation, cancellationToken, statement.TransactionStart));
                }
            }
        }

        (int Start, int End) FrameBounds(SqlWindowFunction function, int position, int start, int end, int peerStart, int peerEnd)
        {
            if (function.Frame is not { } frame) { return (start, peerEnd); }
            var offsets = frameOffsets[frame];
            return (Bound(frame.Syntax.Start, offsets.Start, false), Bound(frame.Syntax.End, offsets.End, true));

            int Bound(SqlWindowFrameBound bound, long offset, bool ending)
            {
                decimal target = bound.Kind switch
                {
                    SqlWindowFrameBoundKind.UnboundedPreceding => start,
                    SqlWindowFrameBoundKind.UnboundedFollowing => end,
                    SqlWindowFrameBoundKind.Preceding => (decimal)position - offset + (ending ? 1 : 0),
                    SqlWindowFrameBoundKind.Following => (decimal)position + offset + (ending ? 1 : 0),
                    _ => frame.Syntax.Mode == SqlWindowFrameMode.Range ? ending ? peerEnd : peerStart : position + (ending ? 1 : 0),
                };
                return (int)Math.Clamp(target, start, end);
            }
        }

        async Task<long> FrameOffsetAsync(SqlBoundExpression? offset)
        {
            if (offset is null) { return 0; }
            await PrepareSubqueriesAsync(offset, [], statement, cancellationToken).ConfigureAwait(false);
            var value = evaluator.Evaluate(offset, []);
            long number = value switch
            {
                null => throw new SqlExecutionException("CS02071", ErrorMessages.WindowFrameNull),
                sbyte n => n, short n => n, int n => n, long n => n,
                _ => throw new SqlExecutionException("CS02070", ErrorMessages.WindowFrameInteger),
            };
            if (number < 0) { throw new SqlExecutionException("CS02072", ErrorMessages.WindowFrameNegative); }
            return number;
        }

        async Task<object?> ValueFrameAsync(SqlWindowFunction function, int position, int first, int last)
        {
            long nth = 1;
            if (function.Name == "NTH_VALUE")
            {
                var current = rows[sorted[position]];
                await PrepareSubqueriesAsync(function.Arguments[1], current, statement, cancellationToken).ConfigureAwait(false);
                var value = evaluator.Evaluate(function.Arguments[1], current);
                if (value is null) { return null; }
                nth = WindowInteger(value, function);
                if (nth <= 0) { throw new SqlExecutionException("CS05082", ErrorMessages.WindowNthPositive, location: function.Expression.Location); }
            }
            if (first >= last || nth > last - first) { return null; }
            int target = function.Name == "LAST_VALUE" ? last - 1 : first + (int)(nth - 1);
            var row = rows[sorted[target]];
            await PrepareSubqueriesAsync(function.Arguments[0], row, statement, cancellationToken).ConfigureAwait(false);
            return NormalizeGroupValue(evaluator.Evaluate(function.Arguments[0], row), function.Type);
        }

        async Task<object?> NavigateAsync(SqlWindowFunction function, int position, int start, int end)
        {
            var args = function.Arguments;
            var current = rows[sorted[position]];
            long offset = 1;
            if (args.Count > 1)
            {
                await PrepareSubqueriesAsync(args[1], current, statement, cancellationToken).ConfigureAwait(false);
                var value = evaluator.Evaluate(args[1], current);
                if (value is null) { return null; }
                offset = WindowInteger(value, function);
            }
            // Decimal arithmetic prevents Int64.MinValue negation or index-add overflow.
            decimal target = position + (function.Name == "LEAD" ? (decimal)offset : -(decimal)offset);
            object? result;
            if (target >= start && target < end)
            {
                var targetRow = rows[sorted[(int)target]];
                await PrepareSubqueriesAsync(args[0], targetRow, statement, cancellationToken).ConfigureAwait(false);
                result = evaluator.Evaluate(args[0], targetRow);
            }
            else if (args.Count > 2)
            {
                await PrepareSubqueriesAsync(args[2], current, statement, cancellationToken).ConfigureAwait(false);
                result = evaluator.Evaluate(args[2], current);
            }
            else { result = null; }
            return NormalizeGroupValue(result, function.Type);
        }
    }

    private static long WindowInteger(object value, SqlWindowFunction function) => value switch
    {
        sbyte number => number, short number => number, int number => number, long number => number,
        _ => throw new SqlExecutionException(function.Name == "NTH_VALUE" ? "CS02073" : "CS02068",
            function.Name == "NTH_VALUE" ? ErrorMessages.WindowNthInteger : ErrorMessages.WindowIntegerArgument, location: function.Expression.Location),
    };

    private static int Tile(int index, int rows, long buckets)
    {
        long small = rows / buckets;
        long largeCount = rows % buckets;
        long largeRows = (small + 1) * largeCount;
        return checked((int)(index < largeRows ? index / (small + 1) + 1 : largeCount + (index - largeRows) / small + 1));
    }

    private static int CompareWindowKey(object? a, object? b, SqlBoundOrdering ordering, Collation collation)
        => a is null ? b is null ? 0 : ordering.NullsFirst ? -1 : 1
            : b is null ? ordering.NullsFirst ? 1 : -1
            : (ordering.IsDescending ? -1 : 1) * Math.Sign(SqlValueComparer.Compare(a, b, collation));
}

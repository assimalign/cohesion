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
        memory.Reserve(128L + 96L * (plan.InputWidth + plan.Projections.Count) + 64L * plan.OrderBy.Count);
        await using var input = (SqlMaterializedResultSet)await ExecuteAsync(plan.Input, statement, cancellationToken).ConfigureAwait(false);
        var evaluator = ExecutionEvaluator(_subqueryValues, cancellationToken);
        var rows = new List<object?[]>();
        int width = plan.InputWidth + plan.Groups.Sum(group => group.Functions.Count);
        long inputBufferBytes = input.RowBufferBytes;
        memory.Reserve(inputBufferBytes); // input and growing augmented list coexist
        for (int index = 0; index < input.Rows.Count; index++)
        {
            var source = input.Rows[index];
            cancellationToken.ThrowIfCancellationRequested();
            var row = new object?[width];
            Array.Copy(source, row, source.Length);
            memory.Retain(row);
            rows.Add(row);
            input.ClearRow(index);
            memory.ReleaseRow(source);
        }
        input.ClearRows();
        memory.Release(inputBufferBytes);
        memory.Reserve(128L + 4L * rows.Count); // exact sort index plus fixed sort scratch
        var sorted = Enumerable.Range(0, rows.Count).ToArray();
        object?[][]? keys = null;
        Collation[]? collations = null;
        long keyBytes = 0;
        var frameOffsets = new Dictionary<SqlWindowFrameBinding, (object Start, object End)>();
        foreach (var group in plan.Groups)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (statement.Metrics.Analysis(group) is { } work) { work.Candidates += rows.Count; }
            var sortWork = statement.Metrics.Analysis(group.Functions);
            if (!group.SortShared)
            {
                if (keyBytes > 0)
                {
                    foreach (var key in keys!) { foreach (var value in key) { if (value is not null) { memory.ReleaseValue(value); } } }
                    keys = null; collations = null; memory.Release(keyBytes); keyBytes = 0;
                }
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
                        if (value is not null) { memory.RetainValue(value); }
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
                    {
                        memory.Reserve(128);
                        var offsets = (await FrameOffsetAsync(frame.StartOffset, frame.Syntax.Mode).ConfigureAwait(false), await FrameOffsetAsync(frame.EndOffset, frame.Syntax.Mode).ConfigureAwait(false));
                        memory.RetainValue(offsets.Item1); memory.RetainValue(offsets.Item2);
                        frameOffsets.Add(frame, offsets);
                    }
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
                long scratch = 768L + group.Functions.Count * 128L;
                memory.Reserve(scratch);
                memory.BeginPartition();
                try { await ComputePartitionAsync(partitionStart, partitionEnd, group, bytes => { memory.Reserve(bytes); scratch += bytes; }).ConfigureAwait(false); }
                finally
                {
                    if (_subqueryValues is { HasCorrelatedQueries: true } frame) { frame.BeginRow([]); }
                    memory.EndPartition();
                    memory.Release(scratch);
                }
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

        async Task ComputePartitionAsync(int start, int end, SqlWindowGroup group, Action<long> reserveScratch)
        {
            int size = end - start;
            var buckets = new long?[group.Functions.Count];
            var accumulators = new SqlAggregateAccumulator?[group.Functions.Count];
            var nextInputs = new int[group.Functions.Count];
            var previousStarts = Enumerable.Repeat(-1, group.Functions.Count).ToArray();
            var previousEnds = Enumerable.Repeat(-1, group.Functions.Count).ToArray();
            var results = new object?[group.Functions.Count];
            reserveScratch(96L + 8L * (size + 1) + 80L * group.Functions.Count);
            var peerGroups = new int[size];
            var groupStarts = new int[size + 1];
            int groupCount = 0;
            for (int at = start; at < end; at++)
            {
                if (at == start || !SameKeys(at - 1, at, group.Partition.Count + group.Ordering.Count)) { groupStarts[groupCount++] = at; }
                peerGroups[at - start] = groupCount - 1;
            }
            groupStarts[groupCount] = end;
            var rangeHeads = new int[group.Functions.Count];
            var rangeTails = new int[group.Functions.Count];
            Array.Fill(rangeHeads, start); Array.Fill(rangeTails, start);
            var cached = new object?[group.Functions.Count][];
            var evaluated = new bool[group.Functions.Count][];
            var moving = new SqlMovingWindowState[group.Functions.Count][];
            var previousFrames = new WindowMembers[group.Functions.Count];
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
                        var (frameStart, frameEnd) = FrameBounds(function, f, position, start, end, peerStart, peerEnd);
                        var members = Members(function, frameStart, frameEnd, position, peerStart, peerEnd);
                        object? result = function.Aggregate is not null
                            ? await AggregateFrameAsync(function, f, members).ConfigureAwait(false)
                            : function.Name is "FIRST_VALUE" or "LAST_VALUE" or "NTH_VALUE"
                                ? await ValueFrameAsync(function, position, members).ConfigureAwait(false)
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
                        if (result is not null) { memory.RetainValue(result); }
                        row[function.Ordinal] = result;
                    }
                }
                peerStart = peerEnd;
            }
            for (int i = 0; i < accumulators.Length; i++)
            {
                if (accumulators[i] is not null) { SqlStringAggregateFunction.ReleaseWindowState(accumulators[i]!); memory.Release(256); }
                if (cached[i] is { } values) { foreach (var value in values) { if (value is not null) { memory.ReleaseValue(value); } } }
            }

            (int Start, int End) FrameBounds(SqlWindowFunction function, int index, int position, int partitionStart, int partitionEnd, int peerFirst, int peerLast)
            {
                if (function.Frame is not { } frame) { return (partitionStart, peerLast); }
                var offsets = frameOffsets[frame];
                return (Bound(frame.Syntax.Start, offsets.Start, false), Bound(frame.Syntax.End, offsets.End, true));

                int Bound(SqlWindowFrameBound bound, object offset, bool ending)
                {
                    if (bound.Kind == SqlWindowFrameBoundKind.UnboundedPreceding) { return partitionStart; }
                    if (bound.Kind == SqlWindowFrameBoundKind.UnboundedFollowing) { return partitionEnd; }
                    if (bound.Kind == SqlWindowFrameBoundKind.CurrentRow)
                    { return frame.Syntax.Mode == SqlWindowFrameMode.Rows ? position + (ending ? 1 : 0) : ending ? peerLast : peerFirst; }
                    if (frame.Syntax.Mode == SqlWindowFrameMode.Range)
                    {
                        var ordering = group.Ordering[0];
                        int key = group.Partition.Count;
                        var basis = keys![sorted[position]][key];
                        int cursor = ending ? rangeTails[index] : rangeHeads[index];
                        bool subtract = (bound.Kind == SqlWindowFrameBoundKind.Preceding) != ordering.IsDescending;
                        bool less = ending != ordering.IsDescending;
                        while (cursor < partitionEnd)
                        {
                            cancellationToken.ThrowIfCancellationRequested();
                            if (statement.Metrics.Analysis(function) is { } boundary) { boundary.WindowBoundaryVisits++; }
                            var value = keys[sorted[cursor]][key];
                            // NULL bases have a peer-only boundary. NULL candidates use
                            // sort placement rather than the key domain's in_range.
                            bool inRange = basis is null ? ending ? cursor < peerLast : cursor >= peerFirst
                                : value is null ? ending ? ordering.NullsFirst : !ordering.NullsFirst
                                : SqlWindowRange.InRange(value, basis, offset, subtract, less);
                            if (ending ? !inRange : inRange) { break; }
                            cursor++;
                        }
                        if (ending) { rangeTails[index] = cursor; } else { rangeHeads[index] = cursor; }
                        return cursor;
                    }
                    decimal target = (frame.Syntax.Mode == SqlWindowFrameMode.Groups ? peerGroups[position - partitionStart] : position) +
                        (bound.Kind == SqlWindowFrameBoundKind.Preceding ? -(decimal)(long)offset : (long)offset) + (ending ? 1 : 0);
                    return frame.Syntax.Mode == SqlWindowFrameMode.Groups ? groupStarts[(int)Math.Clamp(target, 0, groupCount)]
                        : (int)Math.Clamp(target, partitionStart, partitionEnd);
                }
            }

            async Task<object?> MovingFrameAsync(SqlWindowFunction function, int index, WindowMembers members)
            {
                var aggregate = function.Aggregate!;
                if (moving[index] is null)
                {
                    reserveScratch(120L + 8L * size + ((size + 7L) & ~7L));
                    cached[index] = new object?[size]; evaluated[index] = new bool[size];
                    int segments = function.Frame?.Syntax.Exclusion is null or SqlWindowExclusion.NoOthers ? 1 : 3;
                    moving[index] = new SqlMovingWindowState[segments];
                    for (int segment = 0; segment < segments; segment++)
                    { moving[index][segment] = new SqlMovingWindowState(function.Name, aggregate.Collation.Resolve(_subqueryValues), cached[index], reserveScratch, aggregate.Function is SqlStatisticsFunction && function.Type is DatabaseType.Float32 or DatabaseType.Float64); }
                }
                for (int segment = 0; segment < moving[index].Length; segment++)
                {
                    var state = moving[index][segment];
                    var (left, right) = members.Range(segment);
                    left -= start; right -= start;
                    while (state.First < Math.Min(left, state.Last))
                    {
                        cancellationToken.ThrowIfCancellationRequested();
                        if (statement.Metrics.Analysis(function) is { } inverse) { inverse.WindowInverseTransitions++; }
                        state.Remove(state.First++);
                    }
                    if (state.Last < left) { state.First = state.Last = left; }
                    while (state.Last < right)
                    {
                        int at = state.Last++;
                        if (!evaluated[index][at])
                        {
                            cancellationToken.ThrowIfCancellationRequested();
                            if (statement.Metrics.Analysis(function) is { } work) { work.Candidates++; }
                            var inputRow = rows[sorted[start + at]];
                            await PrepareSubqueriesAsync(aggregate.Filter, inputRow, statement, cancellationToken).ConfigureAwait(false);
                            if (evaluator.Matches(aggregate.Filter, inputRow))
                            {
                                foreach (var argument in aggregate.Arguments)
                                { await PrepareSubqueriesAsync(argument, inputRow, statement, cancellationToken).ConfigureAwait(false); }
                                object? value = aggregate.Arguments.Length == 0 ? SqlMovingWindowState.CountedRow : evaluator.Evaluate(aggregate.Arguments[0], inputRow);
                                if (value is not null) { memory.RetainValue(value); }
                                cached[index][at] = value;
                            }
                            evaluated[index][at] = true;
                        }
                        if (statement.Metrics.Analysis(function) is { } transition) { transition.WindowTransitions++; }
                        state.Add(at);
                    }
                    state.First = left;
                }
                previousStarts[index] = members.First; previousEnds[index] = members.Last;
                return results[index] = NormalizeGroupValue(SqlMovingWindowState.Result(moving[index]), function.Type);
            }

            async Task<object?> AggregateFrameAsync(SqlWindowFunction function, int index, WindowMembers members)
            {
                if (previousStarts[index] >= 0 && previousFrames[index] == members) { return results[index]; }
                int first = members.First, last = members.Last;
                previousFrames[index] = members;
                if (SqlStandardLibrary.IgnoresWindowOrdering(function.Aggregate!.Function))
                { return await MovingFrameAsync(function, index, members).ConfigureAwait(false); }
                var aggregate = function.Aggregate!;
                var collation = aggregate.Collation.Resolve(_subqueryValues);
                bool orderedInput = aggregate.OrderBy.Count > 0 && !SqlStandardLibrary.IgnoresWindowOrdering(aggregate.Function);
                bool incremental = !orderedInput && SqlStandardLibrary.Contains(aggregate.Function) && function.Frame?.Syntax.Exclusion is not (SqlWindowExclusion.CurrentRow or SqlWindowExclusion.Group or SqlWindowExclusion.Ties);
                bool recomputing = !incremental || previousStarts[index] >= 0 && previousStarts[index] != first;
                SqlAggregateAccumulator accumulator;
                if (incremental)
                {
                    // Keep transitions while the frame head stays put, including a bounded
                    // PRECEDING head clamped to the partition start. Without an inverse
                    // transition a moved head requires a fresh aggregate over the new frame.
                    if (accumulators[index] is not null && previousStarts[index] != first)
                    { SqlStringAggregateFunction.ReleaseWindowState(accumulators[index]!); accumulators[index] = null; memory.Release(256); }
                    if (accumulators[index] is null) { nextInputs[index] = first; }
                    accumulator = accumulators[index] ??= Create();
                }
                else { accumulator = Create(); }
                long retained = orderedInput ? 128L + 96L * aggregate.OrderBy.Count : 0;
                memory.Reserve(retained);
                try
                {
                    var ordered = orderedInput ? new GroupAggregateState(accumulator, aggregate, _subqueryValues) : null;
                    for (int segment = 0; segment < 3; segment++)
                    {
                        var range = members.Range(segment);
                        for (int inputPosition = Math.Max(range.First, incremental ? nextInputs[index] : range.First); inputPosition < range.Last; inputPosition++)
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
                    if (!incremental) { SqlStringAggregateFunction.ReleaseWindowState(accumulator); memory.Release(256); }
                }

                SqlAggregateAccumulator Create()
                {
                    memory.Reserve(256);
                    return aggregate.Function is SqlStringAggregateFunction text
                        ? text.CreateWindowAccumulator(memory)
                        : aggregate.Function.CreateAccumulator(new SqlFunctionContext(aggregate.Database, collation, cancellationToken, statement.TransactionStart));
                }
            }
        }

        async Task<object> FrameOffsetAsync(SqlBoundExpression? offset, SqlWindowFrameMode mode)
        {
            if (offset is null) { return 0; }
            await PrepareSubqueriesAsync(offset, [], statement, cancellationToken).ConfigureAwait(false);
            var value = evaluator.Evaluate(offset, []);
            if (value is null) { throw new SqlExecutionException("CS02071", ErrorMessages.WindowFrameNull); }
            if (mode == SqlWindowFrameMode.Range) { SqlWindowRange.ValidateOffset(value); return value; }
            long number = value switch
            {
                null => throw new SqlExecutionException("CS02071", ErrorMessages.WindowFrameNull),
                sbyte n => n, short n => n, int n => n, long n => n,
                _ => throw new SqlExecutionException("CS02070", ErrorMessages.WindowFrameInteger),
            };
            if (number < 0) { throw new SqlExecutionException("CS02072", ErrorMessages.WindowFrameNegative); }
            return number;
        }

        async Task<object?> ValueFrameAsync(SqlWindowFunction function, int position, WindowMembers members)
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
            bool reverse = function.Name == "LAST_VALUE";
            bool ignore = function.Expression.NullTreatment == SqlWindowNullTreatment.Ignore;
            for (int part = reverse ? 2 : 0; reverse ? part >= 0 : part < 3; part += reverse ? -1 : 1)
            {
                var (first, last) = members.Range(part);
                if (!ignore)
                {
                    if (nth > last - first) { nth -= last - first; continue; }
                    int target = reverse ? last - (int)nth : first + (int)nth - 1;
                    var row = rows[sorted[target]];
                    await PrepareSubqueriesAsync(function.Arguments[0], row, statement, cancellationToken).ConfigureAwait(false);
                    return NormalizeGroupValue(evaluator.Evaluate(function.Arguments[0], row), function.Type);
                }
                for (int at = reverse ? last - 1 : first; reverse ? at >= first : at < last; at += reverse ? -1 : 1)
                {
                    var row = rows[sorted[at]];
                    await PrepareSubqueriesAsync(function.Arguments[0], row, statement, cancellationToken).ConfigureAwait(false);
                    var value = evaluator.Evaluate(function.Arguments[0], row);
                    if (value is not null && --nth == 0) { return NormalizeGroupValue(value, function.Type); }
                }
            }
            return null;
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
            if (function.Expression.NullTreatment == SqlWindowNullTreatment.Ignore && offset != 0)
            {
                decimal remaining = Math.Abs((decimal)offset);
                int direction = (function.Name == "LEAD" ? 1 : -1) * (offset < 0 ? -1 : 1);
                int at = position;
                while (at + direction >= start && at + direction < end)
                {
                    at += direction;
                    var candidate = rows[sorted[at]];
                    await PrepareSubqueriesAsync(args[0], candidate, statement, cancellationToken).ConfigureAwait(false);
                    var value = evaluator.Evaluate(args[0], candidate);
                    if (value is not null && --remaining == 0) { return NormalizeGroupValue(value, function.Type); }
                }
                target = end;
            }
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

    private static WindowMembers Members(SqlWindowFunction function, int first, int last, int position, int peerFirst, int peerLast)
    {
        last = Math.Max(first, last);
        var exclusion = function.Frame?.Syntax.Exclusion ?? SqlWindowExclusion.NoOthers;
        if (exclusion == SqlWindowExclusion.NoOthers) { return new(first, last, last, last, last, last); }
        int excludedFirst = exclusion == SqlWindowExclusion.CurrentRow ? position : peerFirst;
        int excludedLast = exclusion == SqlWindowExclusion.CurrentRow ? position + 1 : peerLast;
        int middleFirst = exclusion == SqlWindowExclusion.Ties ? Math.Clamp(position, first, last) : last;
        int middleLast = exclusion == SqlWindowExclusion.Ties ? Math.Clamp(position + 1, first, last) : last;
        return new(first, last, Math.Clamp(excludedFirst, first, last), Math.Clamp(excludedLast, first, last), middleFirst, middleLast);
    }

    private readonly record struct WindowMembers(int First, int Last, int ExcludedFirst, int ExcludedLast, int MiddleFirst, int MiddleLast)
    {
        internal (int First, int Last) Range(int segment) => segment switch
        {
            0 => (First, ExcludedFirst),
            1 => (MiddleFirst, MiddleLast),
            _ => (ExcludedLast, Last),
        };
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

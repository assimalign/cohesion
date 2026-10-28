using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.ExceptionServices;
using System.Threading;
using System.Threading.Tasks;

using Assimalign.Cohesion.Database.Execution;
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
            int partitionStart = 0;
            while (partitionStart < sorted.Length)
            {
                cancellationToken.ThrowIfCancellationRequested();
                int partitionEnd = partitionStart + 1;
                while (partitionEnd < sorted.Length && SameKeys(partitionStart, partitionEnd, group.Partition.Count)) { partitionEnd++; }
                // Partition scratch is admitted and released at the partition boundary. Output
                // cells remain in the separately charged input/result rows until the stage ends.
                long scratch = 64L + group.Functions.Count * 32L;
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
                        object? result = function.Name switch
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
        _ => throw new SqlExecutionException("CS02068", ErrorMessages.WindowIntegerArgument, location: function.Expression.Location),
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

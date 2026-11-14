using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

using Assimalign.Cohesion.Database.Storage;

namespace Assimalign.Cohesion.Database.Sql.Internal;

internal sealed partial class SqlPlanExecutor
{
    private static bool CanSpillWindow(SqlWindowAggregatePlan plan)
        => plan.Groups.All(group => group.Partition.Count > 0
            && group.Partition.Concat(group.Ordering).All(key => key.Collation.IsFixed));

    private static bool CanRestartWindow(SqlWindowAggregatePlan plan)
        => plan.Groups.All(group => group.Partition.Concat(group.Ordering).All(key => !WindowRestartSensitive(key.Key))
            && group.Functions.All(function => function.Arguments.All(argument => !WindowRestartSensitive(argument))
                && (function.Frame is not { } frame || !WindowRestartSensitive(frame.StartOffset) && !WindowRestartSensitive(frame.EndOffset))
                && (function.Aggregate is not { } aggregate || SqlStandardLibrary.Contains(aggregate.Function)
                    && !WindowRestartSensitive(aggregate.Filter) && aggregate.OrderBy.All(key => !WindowRestartSensitive(key.Key)))))
            && plan.Projections.All(projection => !WindowRestartSensitive(projection.Value))
            && plan.OrderBy.All(key => !WindowRestartSensitive(key.Key));

    // Extra handoff space is fixed, independent of input/partition count. It covers
    // one writer page, encoding/decoding overlap and sort-key/record construction.
    internal const long WindowSpillScratchBytes = 131072;

    private async Task<SqlWindowInputSpool> SortWindowSpoolAsync(SqlWindowInputSpool input, int width, int oldKeyCount,
        IReadOnlyList<SqlBoundOrdering>? ordering, bool evaluateKeys, SqlWindowGroup group, SqlStatementContext statement,
        SqlExpressionEvaluator evaluator, CancellationToken token)
    {
        long budget = _definitions.StatementMemoryBudget;
        long grant = Math.Min(int.MaxValue, budget - statement.Metrics.HashMemoryBytes - input.ReadBytes - 32768);
        int bound = (int)Math.Min(SqlWindowInputSpool.MaximumRecordBytes, (grant - 32768) / 2 - 8192);
        if (grant < 65536 || bound < 16) { throw SqlExecutionException.StatementMemoryBudget(budget); }
        var columns = ordering?.Select(key => new SqlTempSortKeyCodec.Column(key.IsDescending, key.NullsFirst,
            key.Collation.Resolve(_subqueryValues))).ToArray() ?? [];
        var families = new byte[columns.Length];
        long maximumDecoded = 0;
        statement.Metrics.ReservePlainSelectMemory(grant, budget);
        StorageTempSortReader reader;
        try
        {
            using var sorter = StorageTempSorter.Create(input.Statement, grant, bound, 64);
            foreach (var record in input.Read(token))
            {
                token.ThrowIfCancellationRequested();
                if (record.Length != 1 + oldKeyCount + width || record[0] is not long)
                { throw new SqlExecutionException(StorageTempException.DamageCode, ErrorMessages.SpillRowDamaged); }
                var row = record[(1 + oldKeyCount)..];
                var keys = new object?[columns.Length];
                if (ordering is not null)
                {
                    for (int k = 0; k < keys.Length; k++)
                    {
                        if (evaluateKeys)
                        {
                            await PrepareSubqueriesAsync(ordering[k].Key, row, statement, token).ConfigureAwait(false);
                            keys[k] = evaluator.Evaluate(ordering[k].Key, row);
                        }
                        else { keys[k] = record[1 + k]; }
                    }
                }
                byte[] key;
                object?[] value;
                if (ordering is null)
                {
                    key = new byte[8]; value = record;
                }
                else
                {
                    if (!SqlTempSortKeyCodec.TryEncode(keys, columns, families, out var encodedKey))
                    { throw SqlWindowInputSpool.EncodingRefusal(budget); }
                    key = new byte[encodedKey.Length + 8]; encodedKey.CopyTo(key, 0);
                    value = new object?[1 + keys.Length + width]; value[0] = record[0];
                    keys.CopyTo(value, 1); row.CopyTo(value, 1 + keys.Length);
                }
                BinaryPrimitives.WriteInt64BigEndian(key.AsSpan(key.Length - 8), (long)record[0]!);
                if (!SqlTempRowCodec.TryGetLength(value, out int length) || 16L + key.Length + length > bound
                    || !SqlTempRowCodec.TryEncode(value, out var encoded))
                { throw SqlWindowInputSpool.EncodingRefusal(budget); }
                maximumDecoded = Math.Max(maximumDecoded, SqlTempRowCodec.DecodedBytes(value));
                sorter.Append(key, encoded, token);
            }
            reader = sorter.Complete(token);
            SqlSortSpillMetrics.Record(statement.Metrics, input.Statement, sorter);
            SqlHashSpillMetrics.Sort(statement.Metrics, input.Statement, sorter);
            SqlWindowSpillMetrics.Record(statement.Metrics, group, input.Statement, sorter);
            SqlRowCollector.ObserveSpill?.Invoke(ordering is null ? "window-ordinal" : "window-keys");
        }
        finally { statement.Metrics.ReleasePlainSelectMemory(grant); }
        return new SqlWindowInputSpool(statement.Metrics, budget, input.Statement, reader, maximumDecoded, input.Count);
    }
}

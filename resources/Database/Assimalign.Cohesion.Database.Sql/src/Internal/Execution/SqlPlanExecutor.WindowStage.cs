using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.ExceptionServices;
using System.Threading;
using System.Threading.Tasks;

using Assimalign.Cohesion.Database.Execution;
using Assimalign.Cohesion.Database.Sql.Language;
using Assimalign.Cohesion.Database.Storage;
using Assimalign.Cohesion.Database.Types;

namespace Assimalign.Cohesion.Database.Sql.Internal;

internal sealed partial class SqlPlanExecutor
{
    private static async Task<DatabaseResult> PreserveWindowRefusalAsync(Task<DatabaseResult> execution, SqlExecutionException refusal)
    {
        try { return await execution.ConfigureAwait(false); }
        catch (SqlExecutionException failure) when (!ReferenceEquals(failure, refusal) && SqlWindowInputSpool.IsMemoryRefusal(failure) && !failure.Data.Contains(nameof(SqlTempRowCodec)))
        { refusal.Data[nameof(SqlWindowInputSpool)] = failure; ExceptionDispatchInfo.Throw(refusal); throw; }
        catch (StorageTempException failure) when (failure.Code is StorageTempException.EngineQuotaCode or
            StorageTempException.StatementQuotaCode or StorageTempException.FreeSpaceCode)
        { refusal.Data[nameof(SqlWindowInputSpool)] = failure; ExceptionDispatchInfo.Throw(refusal); throw; }
    }

    private async Task<DatabaseResult> SpillWindowStageAsync(SqlWindowInputSpool source,
        SqlWindowAggregatePlan spilledPlan, long pendingBefore, int augmentedWidth, SqlDatabaseSession? owner,
        SqlStatementContext statement, CancellationToken cancellationToken, SqlWindowMemory memory,
        SqlExpressionEvaluator evaluator, List<object?[]> rows,
        Dictionary<SqlWindowFrameBinding, (object Start, object End)> frameOffsets,
        Func<int, int, SqlWindowGroup, Action<long>, Task> computePartition,
        Func<SqlBoundExpression?, SqlWindowFrameMode, Task<object>> frameOffset,
        Action<int[], object?[][]?, Collation[]?> bindPartition)
    {
        Collation[] collations = [];
        object?[][]? keys = null;
        ResetMemory(spilledPlan);
        using var prepared = new SqlWindowInputSpool(statement.Metrics, _definitions.StatementMemoryBudget, owner!, cancellationToken);
        long ordinal = 0;
        foreach (var raw in source.Read(cancellationToken))
        {
            if (raw.Length != spilledPlan.InputWidth)
            { throw new SqlExecutionException(StorageTempException.DamageCode, ErrorMessages.SpillRowDamaged); }
            var record = new object?[1 + augmentedWidth]; record[0] = ordinal++;
            raw.CopyTo(record, 1); prepared.Append(record, cancellationToken);
        }
        prepared.Seal(cancellationToken); source.Dispose();
        SqlWindowInputSpool current = prepared;
        int savedKeyCount = 0;
        SqlBoundOrdering[] ordering = [];
        try
        {
            foreach (var group in spilledPlan.Groups)
            {
                ResetStage();
                if (statement.Metrics.Analysis(group) is { } work) { work.Candidates += current.Count; }
                if (!group.SortShared)
                {
                    long bytes = 256L + 64L * (group.Partition.Count + group.Ordering.Count);
                    if (bytes > _definitions.StatementMemoryBudget - statement.Metrics.HashMemoryBytes)
                    { throw SqlExecutionException.StatementMemoryBudget(_definitions.StatementMemoryBudget); }
                    ordering = group.Partition.Concat(group.Ordering).ToArray();
                }
                using var ordered = await SortWindowSpoolAsync(current, augmentedWidth, savedKeyCount, ordering,
                    !group.SortShared, group, statement, evaluator, cancellationToken).ConfigureAwait(false);
                current.Dispose();
                savedKeyCount = ordering.Length;
                collations = ordering.Select(key => key.Collation.Resolve(_subqueryValues)).ToArray();
                if (!group.SortShared && statement.Metrics.Analysis(group.Functions) is { } sortWork) { sortWork.Candidates++; }
                if (ordered.Count > 0)
                {
                    foreach (var function in group.Functions)
                    {
                        if (function.Frame is not { } frame || frameOffsets.ContainsKey(frame)) { continue; }
                        memory.Reserve(128);
                        var offsets = (await frameOffset(frame.StartOffset, frame.Syntax.Mode).ConfigureAwait(false),
                            await frameOffset(frame.EndOffset, frame.Syntax.Mode).ConfigureAwait(false));
                        memory.RetainValue(offsets.Item1); memory.RetainValue(offsets.Item2); frameOffsets.Add(frame, offsets);
                    }
                }
                using var output = new SqlWindowInputSpool(statement.Metrics, _definitions.StatementMemoryBudget, owner!, cancellationToken);
                using (var reader = ordered.Read(cancellationToken).GetEnumerator())
                {
                    bool more = reader.MoveNext();
                    while (more)
                    {
                        ResetStage();
                        var partitionKeys = new List<object?[]>();
                        var ordinals = new List<long>();
                        object?[] first = reader.Current;
                        do
                        {
                            cancellationToken.ThrowIfCancellationRequested();
                            var record = reader.Current;
                            if (record.Length != 1 + savedKeyCount + augmentedWidth || record[0] is not long)
                            { throw new SqlExecutionException(StorageTempException.DamageCode, ErrorMessages.SpillRowDamaged); }
                            var row = record[(1 + savedKeyCount)..];
                            var key = record[1..(1 + savedKeyCount)];
                            memory.Reserve(48); // key/ordinal list capacity and growth
                            memory.Retain(row); memory.Retain(key);
                            rows.Add(row); partitionKeys.Add(key); ordinals.Add((long)record[0]!);
                            more = reader.MoveNext();
                        }
                        while (more && SamePartition(first, reader.Current, group.Partition.Count));
                        memory.Reserve(152L + 12L * rows.Count);
                        var indexes = Enumerable.Range(0, rows.Count).ToArray(); keys = partitionKeys.ToArray();
                        bindPartition(indexes, keys, collations);
                        long scratch = 768L + group.Functions.Count * 128L;
                        memory.Reserve(scratch); memory.BeginPartition();
                        try
                        { await computePartition(0, rows.Count, group, bytes => { memory.Reserve(bytes); scratch += bytes; }).ConfigureAwait(false); }
                        finally
                        {
                            if (_subqueryValues is { HasCorrelatedQueries: true } frame) { frame.BeginRow([], RecursiveState?.Memory); }
                            memory.EndPartition(); memory.Release(scratch);
                        }
                        for (int i = 0; i < rows.Count; i++)
                        {
                            var record = new object?[1 + savedKeyCount + augmentedWidth]; record[0] = ordinals[i];
                            keys[i].CopyTo(record, 1); rows[i].CopyTo(record, 1 + savedKeyCount);
                            output.Append(record, cancellationToken);
                        }
                        SqlRowCollector.ObserveBuffer?.Invoke("window-partition", rows);
                        rows.Clear(); rows.Capacity = 0; partitionKeys.Clear(); partitionKeys.Capacity = 0;
                        ordinals.Clear(); ordinals.Capacity = 0; keys = null; bindPartition([], null, null);
                    }
                }
                ResetStage(); output.Seal(cancellationToken);
                SqlRowCollector.ObserveSpill?.Invoke("window-output");
                current = await SortWindowSpoolAsync(output, augmentedWidth, savedKeyCount, null, false,
                    group, statement, evaluator, cancellationToken).ConfigureAwait(false);
                // Complete the whole group, including ordinal restoration, before the
                // next group's offsets/arguments run. Shared keys are carried as cells.
            }
            memory.Dispose();
            var result = MaterializeSelect(OutputRows(), spilledPlan.Projections, spilledPlan.OrderBy, spilledPlan.Limit, spilledPlan.Offset,
                spilledPlan.IsDistinct, evaluator, spilledPlan.OrderByProjections, cancellationToken, spillOwner: owner);
            _subqueryValues?.AdmitPendingStatementMemory(pendingBefore);
            return result;
        }
        finally { bindPartition([], null, null); current.Dispose(); }

        void ResetStage()
        {
            ResetMemory(spilledPlan); memory.Reserve(24L + 8L * ordering.Length);
        }

        IEnumerable<object?[]> OutputRows()
        {
            foreach (var record in current.Read(cancellationToken)) { yield return record[(1 + savedKeyCount)..]; }
        }
        bool SamePartition(object?[] left, object?[] right, int length)
        {
            for (int k = 0; k < length; k++)
            {
                var a = left[1 + k]; var b = right[1 + k];
                if (a is null ? b is not null : b is null || SqlValueComparer.Compare(a, b, collations![k]) != 0) { return false; }
            }
            return true;
        }
        void ResetMemory(SqlWindowAggregatePlan retainedPlan)
        {
            memory.Reset();
            if (_subqueryValues is { MemoryMetrics: null } subqueries)
            { foreach (var values in subqueries.StatementValues.Values) { memory.AdoptResult(values); } }
            if (_materializedRelations is { Reserved: 0 } relations)
            { foreach (var relation in relations.Values) { foreach (var row in relation) { memory.AdoptResult(row); } } }
            memory.Reserve(128L + 96L * (retainedPlan.InputWidth + retainedPlan.Projections.Count) + 64L * retainedPlan.OrderBy.Count);
            foreach (var offsets in frameOffsets.Values)
            { memory.Reserve(128); memory.RetainValue(offsets.Start); memory.RetainValue(offsets.End); }
        }
    }
}

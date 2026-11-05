using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.ExceptionServices;
using System.Threading;

using Assimalign.Cohesion.Database.Execution;
using Assimalign.Cohesion.Database.Storage;

namespace Assimalign.Cohesion.Database.Sql.Internal;

internal sealed partial class SqlPlanExecutor
{
    private DatabaseResult MaterializeSortHandoff(SqlSortBudgetHandoff handoff, List<object?[]> rows,
        ref SqlSelectMemory sourceMemory, IReadOnlyList<SqlProjection> projections,
        IReadOnlyList<SqlBoundOrdering> orderBy, SqlRowCount? limit, SqlRowCount? offset, bool distinct,
        SqlExpressionEvaluator evaluator, CancellationToken token, SqlDatabaseSession owner)
    {
        // Own the source ledger locally: the draining iterator must release each row before
        // CreateWithSortedPrefix allocates its arena. No page latch spans a temp-space call;
        // source scans have completed, then statement/run locks precede private file I/O.
        var memory = sourceMemory; sourceMemory = default;
        long budget = _definitions.StatementMemoryBudget;
        long other = _metrics!.HashMemoryBytes - memory.Bytes;
        long arena = 0, scratch = 0;
        SqlStagedSortSource? source = null;
        StorageTempSorter? sorter = null;
        Exception? failure = null;
        try
        {
            int maximum = 0;
            for (int i = handoff.Count; i < rows.Count; i++)
            {
                token.ThrowIfCancellationRequested();
                if (!SqlTempRowCodec.TryGetLength(rows[i], out int length)) { throw handoff.Failure; }
                maximum = Math.Max(maximum, length);
            }
            long readBytes = handoff.Count < rows.Count ? StorageTempSpace.PageSize + (long)maximum : 0;
            long grant = Math.Min(int.MaxValue, budget - other - readBytes);
            int bound = (int)((grant - 32768) / 16 - 8192);
            if (grant < 65536 || bound < 16 || maximum + 16L > bound) { throw handoff.Failure; }
            var collations = orderBy.Select(order => order.Collation.Resolve(evaluator.SubqueryValues)).ToArray();
            var columns = orderBy.Select((order, i) => new SqlTempSortKeyCodec.Column(order.IsDescending, order.NullsFirst, collations[i])).ToArray();
            var families = new byte[columns.Length];
            for (int i = 0; i < handoff.Count; i++)
            {
                var entry = handoff.Buffer![handoff.Offset + i];
                if (!SqlTempSortKeyCodec.TryEncode(entry.Keys, columns, families, out var key)
                    || !SqlTempRowCodec.TryGetLength(entry.Row, out int size) || 16L + key.Length + size > bound)
                { throw handoff.Failure; }
            }

            _metrics.ReservePlainSelectMemory(handoff.ScratchBytes, budget); scratch = handoff.ScratchBytes;
            source = new SqlStagedSortSource(owner, _metrics, budget, budget - other);
            int remaining = rows.Count - handoff.Count;
            for (int i = handoff.Count; i < rows.Count; i++)
            {
                source.Append(rows[i], token); DrainRow(i);
            }
            source.Seal(token);
            int[]? order = Enumerable.Range(0, handoff.Count).ToArray();
            try
            {
                Array.Sort(order, (left, right) =>
                {
                    token.ThrowIfCancellationRequested();
                    var a = handoff.Buffer![handoff.Offset + left].Keys;
                    var b = handoff.Buffer[handoff.Offset + right].Keys;
                    for (int i = 0; i < columns.Length; i++)
                    {
                        int compared = CompareOrderingValues(a[i], b[i], orderBy[i].IsDescending ? -1 : 1,
                            orderBy[i].NullsFirst ? -1 : 1, collations[i]);
                        if (compared != 0) { return compared; }
                    }
                    return left.CompareTo(right);
                });
            }
            catch (InvalidOperationException exception) when (exception.InnerException is DatabaseException or OperationCanceledException)
            { ExceptionDispatchInfo.Throw(exception.InnerException); throw; }

            SqlRowCollector.ObserveSpill?.Invoke("handoff");
            sorter = StorageTempSorter.CreateWithSortedPrefix(source.Statement, grant, bound, DrainPrefix(), 64, token);
            _metrics.ReservePlainSelectMemory(grant, budget); arena = grant;
            SqlRowCollector.ObserveSpill?.Invoke("arena");
            if (remaining != 0)
            {
                foreach (var row in source.Read(token))
                {
                    var keys = new object?[orderBy.Count];
                    for (int i = 0; i < keys.Length; i++) { token.ThrowIfCancellationRequested(); keys[i] = evaluator.Evaluate(orderBy[i].Key, row); }
                    if (!SqlTempSortKeyCodec.TryEncode(keys, columns, families, out var key)
                        || !SqlTempRowCodec.TryGetLength(row, out int length) || 16L + key.Length + length > bound
                        || !SqlTempRowCodec.TryEncode(row, out var value)) { throw handoff.Failure; }
                    SqlRowCollector.ObserveSpill?.Invoke("append"); sorter.Append(key, value, token);
                }
            }
            SqlRowCollector.ObserveSpill?.Invoke("complete");
            var reader = sorter.Complete(token);
            SqlSortSpillMetrics.Record(_metrics, source.Statement, sorter);
            sorter.Dispose(); sorter = null;
            _metrics.ReleasePlainSelectMemory(arena); arena = 0;
            source.Transfer();
            SqlSpilledResultSet result;
            try
            { result = new SqlSpilledResultSet(((DatabaseResultSet)MaterializeProjected(projections, [])).Columns,
                source.Statement, reader, offset?.Count ?? 0, limit?.Count ?? -1, owner); }
            catch (Exception exception) { SqlSpilledResultSet.ReleaseAfterFailure(source.Statement, exception); throw; }
            try
            {
                result.Transform(row => ProjectRow(row, projections, evaluator, token), _metrics, budget, bound, token);
                if (distinct) { result.Deduplicate(new GroupKeyComparer(projections.Select(p => p.Collation.Resolve(evaluator.SubqueryValues)).ToArray()), _metrics, budget, token); }
                result.CaptureMetrics(_metrics); return result;
            }
            catch (Exception exception) { result.DisposeAfterFailure(exception); throw; }

            IEnumerable<(ReadOnlyMemory<byte> Key, ReadOnlyMemory<byte> Value)> DrainPrefix()
            {
                for (int i = 0; i < order!.Length; i++)
                {
                    int index = order[i];
                    var entry = handoff.Buffer![handoff.Offset + index];
                    if (!SqlTempSortKeyCodec.TryEncode(entry.Keys, columns, families, out var key)
                        || !SqlTempRowCodec.TryEncode(entry.Row, out var value)) { throw handoff.Failure; }
                    handoff.Buffer[handoff.Offset + index] = default;
                    DrainRow(index); entry = default;
                    yield return (key, value);
                }
                rows.Clear(); rows.Capacity = 0; memory.Clear();
                handoff.Buffer = null; order = null;
                _metrics.ReleasePlainSelectMemory(scratch); scratch = 0;
                SqlRowCollector.ObserveSpill?.Invoke("drained");
            }
            void DrainRow(int index)
            {
                long bytes = memory.RemoveRow(rows[index]); rows[index] = [];
                _metrics.ReleasePlainSelectMemory(bytes); memory.Bytes -= bytes;
            }
        }
        catch (StorageTempException exception) { failure = SqlSpilledResultSet.Translate(exception); throw failure; }
        catch (AggregateException exception) { failure = SqlSpilledResultSet.TranslateCleanup(exception); throw failure; }
        catch (Exception exception) { failure = exception; throw; }
        finally
        {
            handoff.Buffer = null;
            try
            {
                SqlExecutionException? cleanupFailure = null;
                try { sorter?.Dispose(); }
                catch (AggregateException exception)
                {
                    cleanupFailure = SqlSpilledResultSet.TranslateCleanup(exception);
                }
                try { source?.Dispose(); }
                catch (SqlExecutionException cleanup)
                {
                    if (cleanupFailure is null) { cleanupFailure = cleanup; }
                    else { cleanupFailure.Data[nameof(SqlStagedSortSource)] = cleanup; }
                }
                if (cleanupFailure is not null)
                {
                    if (failure is null) { throw cleanupFailure; }
                    failure.Data[nameof(StorageTempSorter)] = cleanupFailure;
                }
            }
            finally
            {
                _metrics.ReleasePlainSelectMemory(arena + scratch);
                ReleaseSelectMemory(ref memory);
            }
        }
    }
}

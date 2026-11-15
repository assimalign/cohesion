using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using System.Runtime.ExceptionServices;
using System.Threading;

using Assimalign.Cohesion.Database.Storage;

namespace Assimalign.Cohesion.Database.Sql.Internal;

// Filtered inner rows are written once. Each outer row gets an independent reader;
// the reader allowance is held until the join releases this buffer, not per pass.
[System.Diagnostics.DebuggerDisplay("SQL nested-loop inner")]
[System.Diagnostics.DebuggerTypeProxy(typeof(DebugView))]
internal sealed class SqlJoinInnerSpool : IDisposable
{
    private static ConditionalWeakTable<List<object?[]>, SqlJoinInnerSpool>? _buffers;
    private readonly SqlStatementMetrics _metrics;
    private readonly long _budget;
    private readonly StorageTempStatement _statement;
    private readonly StorageTempRun _run;
    private readonly SqlExecutionException _refusal;
    private byte[]? _scratch = new byte[16384];
    private long _maximumDecoded;
    private long _reserved;
    private bool _sealed;

    private SqlJoinInnerSpool(SqlStatementMetrics metrics, long budget, SqlDatabaseSession owner,
        SqlExecutionException refusal, CancellationToken token)
    {
        _metrics = metrics; _budget = budget; _refusal = refusal;
        SqlOperatorSpillRefusal.Record(metrics, refusal);
        _statement = SqlHashSpillMetrics.JoinTemp(metrics, owner);
        _run = _statement.CreateRun(token);
    }

    internal static SqlJoinInnerSpool? For(List<object?[]> rows)
        => _buffers?.TryGetValue(rows, out var spool) == true ? spool : null;

    internal static void Add(List<object?[]> rows, object?[] pending, ref SqlSelectMemory memory,
        SqlStatementMetrics metrics, long budget, SqlDatabaseSession owner, SqlExecutionException refusal, CancellationToken token)
    {
        SqlJoinInnerSpool? spool = null;
        try
        {
            spool = new(metrics, budget, owner, refusal, token);
            // Validate the evaluated prefix before quota admission, as output spools do.
            foreach (var row in rows) { spool.Encode(row); }
            spool.Encode(pending);
            for (int i = 0; i < rows.Count; i++)
            {
                spool.Append(rows[i], token);
                long bytes = memory.RemoveRow(rows[i]);
                rows[i] = []; memory.Bytes -= bytes; metrics.ReleasePlainSelectMemory(bytes);
            }
            rows.Clear(); rows.Capacity = 0; memory.Clear();
            spool.Append(pending, token);
            LazyInitializer.EnsureInitialized(ref _buffers).Add(rows, spool);
        }
        catch (StorageTempException failure)
        { spool?.Dispose(); refusal.Data[nameof(SqlJoinInnerSpool)] = failure; ExceptionDispatchInfo.Throw(refusal); }
        catch
        { spool?.Dispose(); throw; }
    }

    internal void Append(object?[] row, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        int size = Encode(row);
        try { _run.Append(_scratch.AsSpan(0, size), token); }
        catch (StorageTempException failure) { Refuse(failure); }
        _maximumDecoded = Math.Max(_maximumDecoded, SqlTempRowCodec.DecodedBytes(row));
        SqlRowCollector.ObserveBuffer?.Invoke("inner-append", row);
    }

    private int Encode(object?[] row)
    {
        if (!SqlTempRowCodec.TryWrite(row, _scratch, out int size))
        {
            if (!SqlTempRowCodec.TryGetLength(row, out _)) { _refusal.Data[nameof(SqlTempRowCodec)] = true; }
            Refuse(SqlExecutionException.StatementMemoryBudget(_budget));
        }
        if (StorageTempSpace.PageSize + size + 256L + 2 * SqlTempRowCodec.DecodedBytes(row) > _budget)
        { Refuse(SqlExecutionException.StatementMemoryBudget(_budget)); }
        return size;
    }

    internal void Seal(CancellationToken token)
    {
        if (_sealed) { return; }
        try
        {
            _run.Seal(token); _scratch = null;
            long bytes = StorageTempSpace.PageSize + _run.MaximumRecordLength + 256L + 2 * _maximumDecoded;
            _metrics.ReservePlainSelectMemory(bytes, _budget); _reserved = bytes; _sealed = true;
            SqlSortSpillMetrics.RecordOutput(_metrics, _statement);
            SqlRowCollector.ObserveSpill?.Invoke("join-inner");
        }
        catch (StorageTempException failure) { Refuse(failure); }
        catch (SqlExecutionException failure) when (!ReferenceEquals(failure, _refusal)) { Refuse(failure); }
    }

    internal IEnumerable<object?[]> Read(CancellationToken token)
    {
        Seal(token);
        using var reader = OpenReader();
        var buffer = new byte[_run.MaximumRecordLength];
        while (ReadNext(reader, buffer, token, out int size))
        {
            if (!SqlTempRowCodec.TryDecode(buffer.AsSpan(0, size), out var row))
            { Refuse(new SqlExecutionException(StorageTempException.DamageCode, ErrorMessages.SpillRowDamaged)); }
            SqlRowCollector.ObserveBuffer?.Invoke("inner-decoded", row);
            yield return row;
        }
    }

    private StorageTempRunReader OpenReader()
    {
        try { return _run.OpenReader(StorageTempSpace.PageSize); }
        catch (StorageTempException failure) { Refuse(failure); throw; }
    }
    private bool ReadNext(StorageTempRunReader reader, byte[] buffer, CancellationToken token, out int size)
    {
        try { return reader.TryRead(buffer, out size, token); }
        catch (StorageTempException failure) { Refuse(failure); throw; }
    }
    private void Refuse(Exception cause)
    { _refusal.Data[nameof(SqlJoinInnerSpool)] = cause; ExceptionDispatchInfo.Throw(_refusal); }

    internal static void Release(List<object?[]>? rows)
    {
        if (rows is null || For(rows) is not { } spool) { return; }
        _buffers!.Remove(rows); spool.Dispose();
    }
    public void Dispose()
    {
        _scratch = null;
        try { _run.Dispose(); }
        catch (Exception failure) when (failure is StorageTempException or AggregateException)
        { _refusal.Data["InnerCleanup"] = failure; }
        finally { _metrics.ReleasePlainSelectMemory(_reserved); _reserved = 0; }
    }
    private sealed class DebugView { public DebugView(SqlJoinInnerSpool value) { } }
}

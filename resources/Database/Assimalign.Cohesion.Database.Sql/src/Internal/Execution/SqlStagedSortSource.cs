using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using System.Threading;

using Assimalign.Cohesion.Database.Storage;

namespace Assimalign.Cohesion.Database.Sql.Internal;

// A staged root must finish its predicates before evaluating keys/volatile projections.
// This input-order run preserves that boundary when its source list refuses admission.
[System.Diagnostics.DebuggerDisplay("SQL staged sort source")]
[System.Diagnostics.DebuggerTypeProxy(typeof(SqlStagedSortSource.DebugView))]
internal sealed class SqlStagedSortSource : IDisposable
{
    private static readonly ConditionalWeakTable<List<object?[]>, SqlStagedSortSource> _sources = new();
    private readonly StorageTempRun _run;
    private readonly int _bound;
    private readonly long _budget;
    private readonly SqlStatementMetrics _metrics;
    private bool _transferred;
    internal StorageTempStatement Statement { get; }
    internal SqlDatabaseSession Owner { get; }
    internal long MaximumDecodedRowBytes { get; private set; }
    internal long ReadBufferBytes => StorageTempSpace.PageSize + (long)_run.MaximumRecordLength;

    internal SqlStagedSortSource(SqlDatabaseSession owner, SqlStatementMetrics metrics, long budget, long grant)
    {
        SqlSpillLifetime.CheckCapacity(owner);
        Owner = owner; _metrics = metrics; _budget = budget;
        long bound = (Math.Min(grant, int.MaxValue) - 32768) / 16 - 8192;
        if (grant < 65536 || bound < 16) { Refuse(); }
        _bound = (int)bound;
        Statement = owner.Database.Engine.TempSpace!.CreateStatement(owner.Database.Engine.StatementTempSpaceQuota);
        try { _run = Statement.CreateRun(); }
        catch (StorageTempException exception) { var failure = SqlSpilledResultSet.Translate(exception); SqlSpilledResultSet.ReleaseAfterFailure(Statement, failure); throw failure; }
        catch (Exception exception) { SqlSpilledResultSet.ReleaseAfterFailure(Statement, exception); throw; }
    }
    internal static SqlStagedSortSource? For(List<object?[]> rows) => _sources.TryGetValue(rows, out var value) ? value : null;
    internal static void Add(List<object?[]> rows, object?[] row, ref SqlSelectMemory memory,
        SqlStatementMetrics metrics, long budget, SqlDatabaseSession owner, CancellationToken token)
    {
        if (_sources.TryGetValue(rows, out var existing)) { existing.Append(row, token); return; }
        var source = new SqlStagedSortSource(owner, metrics, budget, budget - metrics.HashMemoryBytes + memory.Bytes);
        try
        {
            for (int i = 0; i < rows.Count; i++)
            {
                source.Append(rows[i], token);
                long bytes = memory.RemoveRow(rows[i]); rows[i] = [];
                metrics.ReleasePlainSelectMemory(bytes); memory.Bytes -= bytes;
            }
            rows.Clear(); rows.Capacity = 0; memory.Clear();
            SqlRowCollector.ObserveSpill?.Invoke("staged-drained");
            source.Append(row, token); _sources.Add(rows, source);
        }
        catch (Exception exception) { source.DisposeAfterFailure(exception); throw; }
    }
    internal void Append(object?[] row, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        if (!SqlTempRowCodec.TryGetLength(row, out int length) || length + 16L > _bound || !SqlTempRowCodec.TryEncode(row, out var bytes)) { Refuse(); return; }
        MaximumDecodedRowBytes = Math.Max(MaximumDecodedRowBytes, SqlTempRowCodec.DecodedBytes(row));
        try { _run.Append(bytes, token); }
        catch (StorageTempException exception) { throw SqlSpilledResultSet.Translate(exception); }
    }
    internal void Seal(CancellationToken token)
    {
        try { _run.Seal(token); }
        catch (StorageTempException exception) { throw SqlSpilledResultSet.Translate(exception); }
    }
    internal IEnumerable<object?[]> Read(CancellationToken token)
    {
        _metrics.ReservePlainSelectMemory(ReadBufferBytes, _budget);
        try
        {
            using var reader = _run.OpenReader(StorageTempSpace.PageSize);
            var bytes = new byte[_run.MaximumRecordLength];
            while (ReadNext(reader, bytes, token, out int length))
            {
                if (!SqlTempRowCodec.TryDecode(bytes.AsSpan(0, length), out var row))
                { throw new SqlExecutionException(StorageTempException.DamageCode, ErrorMessages.SpillRowDamaged); }
                yield return row;
            }
        }
        finally { _metrics.ReleasePlainSelectMemory(ReadBufferBytes); }
    }
    private static bool ReadNext(StorageTempRunReader reader, byte[] bytes, CancellationToken token, out int length)
    {
        try { return reader.TryRead(bytes, out length, token); }
        catch (StorageTempException exception) { throw SqlSpilledResultSet.Translate(exception); }
    }
    internal void Transfer()
    {
        try { _run.Dispose(); _transferred = true; }
        catch (StorageTempException exception) { throw SqlSpilledResultSet.Translate(exception); }
    }
    internal StorageTempRun TransferOutput() { _transferred = true; return _run; }
    internal static void Release(List<object?[]> rows)
    {
        if (_sources.TryGetValue(rows, out var source)) { _sources.Remove(rows); source.Dispose(); }
    }
    internal static void ReleaseAfterFailure(List<object?[]> rows, Exception failure)
    {
        if (_sources.TryGetValue(rows, out var source)) { _sources.Remove(rows); source.DisposeAfterFailure(failure); }
    }
    private void DisposeAfterFailure(Exception failure)
    {
        try { Dispose(); }
        catch (SqlExecutionException cleanup) { failure.Data[nameof(SqlStagedSortSource)] = cleanup; }
    }
    public void Dispose()
    {
        try { if (!_transferred) { Statement.Dispose(); } }
        catch (AggregateException exception) { throw SqlSpilledResultSet.TranslateCleanup(exception); }
    }
    private void Refuse() => throw SqlExecutionException.StatementMemoryBudget(_budget);
    private sealed class DebugView { public DebugView(SqlStagedSortSource target) { } }
}

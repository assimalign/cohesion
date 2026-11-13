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
    internal const long WriterBytes = 8704;
    internal const long OperatorBytes = 1280;
    private static readonly ConditionalWeakTable<List<object?[]>, SqlStagedSortSource> _sources = new();
    private readonly StorageTempRun _run;
    private readonly int _bound;
    private readonly long _budget;
    private readonly SqlStatementMetrics _metrics;
    private bool _transferred;
    private readonly bool _sharedStatement;
    private long _writerBytes;
    private long _operatorBytes;
    internal StorageTempStatement Statement { get; }
    internal bool SharedStatement => _sharedStatement;
    internal SqlDatabaseSession Owner { get; }
    internal long MaximumDecodedRowBytes { get; private set; }
    internal long ReadBufferBytes => StorageTempSpace.PageSize + (long)_run.MaximumRecordLength;
    internal long ReadReservation => ReadBufferBytes + (_sharedStatement ? 256 + 2 * MaximumDecodedRowBytes : 0);

    internal SqlStagedSortSource(SqlDatabaseSession owner, SqlStatementMetrics metrics, long budget, long grant)
    {
        System.Diagnostics.Debug.Assert(SqlHashSpillMetrics.For(metrics)?.HasOptionalArenas != true);
        SqlSpillLifetime.CheckCapacity(owner);
        Owner = owner; _metrics = metrics; _budget = budget;
        long bound = (Math.Min(grant, int.MaxValue) - 32768) / 16 - 8192;
        if (grant < 65536 || bound < 16) { Refuse(); }
        _bound = (int)bound;
        var shared = SqlHashSpillMetrics.For(metrics)?.JoinStatement;
        _sharedStatement = shared is not null;
        Statement = shared ?? owner.Database.Engine.TempSpace!.CreateStatement(owner.Database.Engine.StatementTempSpaceQuota);
        try { _run = Statement.CreateRun(); }
        catch (StorageTempException exception)
        { var failure = SqlSpilledResultSet.Translate(exception); if (!_sharedStatement) { SqlSpilledResultSet.ReleaseAfterFailure(Statement, failure); } throw failure; }
        catch (Exception exception) { if (!_sharedStatement) { SqlSpilledResultSet.ReleaseAfterFailure(Statement, exception); } throw; }
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
            source.ReserveWriter();
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
        finally { ReleaseWriter(); }
        if (_sharedStatement) { SqlHashSpillMetrics.RefreshJoin(_metrics, Statement); }
    }
    internal void ReserveWriter()
    {
        if (!_sharedStatement) { return; }
        if (_operatorBytes == 0)
        { _metrics.ReservePlainSelectMemory(OperatorBytes, _budget); _operatorBytes = OperatorBytes; }
        if (_writerBytes != 0) { return; }
        _metrics.ReservePlainSelectMemory(WriterBytes, _budget);
        _writerBytes = WriterBytes;
    }
    private void ReleaseWriter()
    { _metrics.ReleasePlainSelectMemory(_writerBytes); _writerBytes = 0; }
    internal void ReleaseOperator()
    { _metrics.ReleasePlainSelectMemory(_operatorBytes); _operatorBytes = 0; }
    internal IEnumerable<object?[]> Read(CancellationToken token)
    {
        long reservation = ReadReservation;
        _metrics.ReservePlainSelectMemory(reservation, _budget);
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
        finally { _metrics.ReleasePlainSelectMemory(reservation); }
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
        finally { ReleaseOperator(); }
    }
    internal StorageTempRun TransferOutput() { _transferred = true; ReleaseOperator(); return _run; }
    internal static void Detach(List<object?[]> rows) => _sources.Remove(rows);
    internal static void Release(List<object?[]> rows)
    {
        if (_sources.TryGetValue(rows, out var source)) { _sources.Remove(rows); source.Dispose(); }
    }
    internal static void ReleaseAfterFailure(List<object?[]> rows, Exception failure)
    {
        if (_sources.TryGetValue(rows, out var source)) { _sources.Remove(rows); source.DisposeAfterFailure(failure); }
    }
    internal void DisposeAfterFailure(Exception failure)
    {
        try { Dispose(); }
        catch (SqlExecutionException cleanup) { failure.Data[nameof(SqlStagedSortSource)] = cleanup; }
    }
    public void Dispose()
    {
        try { if (!_transferred) { if (_sharedStatement) { _run.Dispose(); } else { Statement.Dispose(); } } }
        catch (StorageTempException exception) { throw SqlSpilledResultSet.Translate(exception); }
        catch (AggregateException exception) { throw SqlSpilledResultSet.TranslateCleanup(exception); }
        finally { ReleaseWriter(); ReleaseOperator(); }
    }
    private void Refuse() => throw SqlExecutionException.StatementMemoryBudget(_budget);
    private sealed class DebugView { public DebugView(SqlStagedSortSource target) { } }
}

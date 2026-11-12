using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using System.Threading;

using Assimalign.Cohesion.Database.Execution;
using Assimalign.Cohesion.Database.Storage;

namespace Assimalign.Cohesion.Database.Sql.Internal;

// Fully evaluated rows in encounter order. Created only after the original row
// admission refuses; fitting lists, collectors and their charges remain unchanged.
[System.Diagnostics.DebuggerDisplay("SQL output spool")]
[System.Diagnostics.DebuggerTypeProxy(typeof(SqlOutputSpool.DebugView))]
internal sealed class SqlOutputSpool : IDisposable
{
    private static ConditionalWeakTable<List<object?[]>, SqlOutputSpool>? _outputs;
    private const long writerBytes = StorageTempSpace.PageSize + 1792;
    private const int maximumRecordBytes = 16384;
    private readonly SqlDatabaseSession _owner;
    private readonly SqlStatementMetrics _metrics;
    private readonly long _budget;
    private readonly StorageTempStatement _statement;
    private readonly StorageTempRun _run;
    private bool _shared;
    private long _reserved;
    private long _maximumDecoded;
    private bool _transferred;

    private SqlOutputSpool(SqlDatabaseSession owner, SqlStatementMetrics metrics, long budget)
    {
        SqlSpillLifetime.CheckCapacity(owner);
        _owner = owner; _metrics = metrics; _budget = budget;
        var shared = SqlHashSpillMetrics.For(metrics)?.JoinStatement;
        _shared = shared is not null;
        _statement = shared ?? owner.Database.Engine.TempSpace!.CreateStatement(owner.Database.Engine.StatementTempSpaceQuota);
        try { _run = _statement.CreateRun(); }
        catch (Exception failure)
        { if (!_shared) { SqlSpilledResultSet.ReleaseAfterFailure(_statement, failure); } throw; }
    }

    internal static SqlOutputSpool? For(List<object?[]> rows) => _outputs?.TryGetValue(rows, out var output) == true ? output : null;

    internal static SqlSpilledResultSet SpillMaterialized(SqlMaterializedResultSet source, SqlStatementMetrics metrics,
        long budget, SqlDatabaseSession owner, ref long reserved, CancellationToken token)
    {
        foreach (var row in source.Rows) { token.ThrowIfCancellationRequested(); Validate(row, budget, out _, out _); }
        using var output = new SqlOutputSpool(owner, metrics, budget);
        try
        {
            for (int i = 0; i < source.Rows.Count; i++)
            { output.Append(source.Rows[i], token, draining: true); source.ClearRow(i); }
            source.ClearRows(); metrics.ReleasePlainSelectMemory(reserved); reserved = 0;
            return output.Take(source.Columns, token);
        }
        catch (Exception failure) { output.DisposeAfterFailure(failure); throw; }
    }

    internal static void Add(List<object?[]> rows, object?[] pending, ref SqlSelectMemory memory,
        SqlStatementMetrics metrics, long budget, SqlDatabaseSession owner, CancellationToken token)
    {
        if (_outputs?.TryGetValue(rows, out var existing) == true) { existing.Append(pending, token); return; }
        foreach (var row in rows) { token.ThrowIfCancellationRequested(); Validate(row, budget, out _, out _); }
        Validate(pending, budget, out _, out _);
        var output = new SqlOutputSpool(owner, metrics, budget);
        try
        {
            // The retiring list pays for its rows while they drain. The one writer and
            // current encoding are bounded handoff scratch; no second row collection.
            for (int i = 0; i < rows.Count; i++)
            {
                output.Append(rows[i], token, draining: true);
                long bytes = memory.RemoveRow(rows[i]); rows[i] = [];
                metrics.ReleasePlainSelectMemory(bytes); memory.Bytes -= bytes;
            }
            rows.Clear(); rows.Capacity = 0; memory.Clear();
            output._reserved = output.ReserveFixed(writerBytes);
            SqlRowCollector.ObserveSpill?.Invoke("output-drained");
            output.Append(pending, token); LazyInitializer.EnsureInitialized(ref _outputs).Add(rows, output);
        }
        catch (Exception failure) { output.DisposeAfterFailure(failure); throw; }
    }

    internal void Append(object?[] row, CancellationToken token, bool draining = false)
    {
        token.ThrowIfCancellationRequested();
        Validate(row, _budget, out int length, out long decoded);
        // Sequential runs need no merge arena or fixed sort fan-in. Admit their
        // actual encoding and decoding workspace, including an old/new row overlap.
        long scratch = draining ? 0 : ReserveFixed(length + 24L);
        try
        {
            if (!SqlTempRowCodec.TryEncode(row, out var bytes)) { Refuse(); return; }
            _run.Append(bytes, token); _maximumDecoded = Math.Max(_maximumDecoded, decoded);
            SqlRowCollector.ObserveSpill?.Invoke("output-append");
        }
        catch (StorageTempException failure) { throw SqlSpilledResultSet.Translate(failure); }
        finally { _metrics.ReleasePlainSelectMemory(scratch); }
    }

    internal SqlSpilledResultSet Take(IReadOnlyList<DatabaseColumn> columns, CancellationToken token, long offset = 0, long limit = -1)
    {
        try
        {
            _run.Seal(token); Release();
            SqlSortSpillMetrics.RecordOutput(_metrics, _statement);
            SqlRowCollector.ObserveSpill?.Invoke("output-spool");
            var result = new SqlSpilledResultSet(columns, _statement, _run, offset, limit, _owner,
                _budget, _maximumDecoded, ownsStatement: !_shared);
            _transferred = true; result.CaptureMetrics(_metrics); return result;
        }
        catch (StorageTempException failure) { throw SqlSpilledResultSet.Translate(failure); }
        catch (AggregateException failure) { throw SqlSpilledResultSet.TranslateCleanup(failure); }
    }

    internal IEnumerable<object?[]> Read(CancellationToken token)
    {
        Seal(token); Release();
        _shared = SqlHashSpillMetrics.AdoptTemp(_metrics, _statement);
        long bytes = StorageTempSpace.PageSize + _run.MaximumRecordLength + 256L + 2 * _maximumDecoded;
        _metrics.ReservePlainSelectMemory(bytes, _budget);
        try
        {
            using var reader = _run.OpenReader(StorageTempSpace.PageSize);
            var buffer = new byte[_run.MaximumRecordLength];
            while (ReadNext(reader, buffer, token, out int length))
            {
                if (!SqlTempRowCodec.TryDecode(buffer.AsSpan(0, length), out var row))
                { throw new SqlExecutionException(StorageTempException.DamageCode, ErrorMessages.SpillRowDamaged); }
                yield return row;
            }
        }
        finally { _metrics.ReleasePlainSelectMemory(bytes); }
    }

    private void Seal(CancellationToken token)
    {
        try { _run.Seal(token); }
        catch (StorageTempException failure) { throw SqlSpilledResultSet.Translate(failure); }
    }
    private static bool ReadNext(StorageTempRunReader reader, byte[] buffer, CancellationToken token, out int length)
    {
        try { return reader.TryRead(buffer, out length, token); }
        catch (StorageTempException failure) { throw SqlSpilledResultSet.Translate(failure); }
    }

    internal static void Release(List<object?[]> rows, Exception? failure = null)
    {
        var outputs = _outputs;
        if (outputs is null || !outputs.TryGetValue(rows, out var output)) { return; }
        outputs.Remove(rows);
        if (failure is not null) { output.DisposeAfterFailure(failure); }
        else { output.Dispose(); }
    }

    private void Release() { _metrics.ReleasePlainSelectMemory(_reserved); _reserved = 0; }
    private static void Validate(object?[] row, long budget, out int length, out long decoded)
    {
        if (!SqlTempRowCodec.TryGetLength(row, out length) || length > maximumRecordBytes)
        { throw SqlExecutionException.StatementMemoryBudget(budget); }
        decoded = SqlTempRowCodec.DecodedBytes(row);
        if (writerBytes + length + 24L + 2 * decoded > budget)
        { throw SqlExecutionException.StatementMemoryBudget(budget); }
    }
    private long ReserveFixed(long bytes)
    {
        if (bytes > _budget - _metrics.HashMemoryBytes && SqlHashSpillMetrics.For(_metrics)?.HasOptionalArenas != true)
        { return 0; }
        try { _metrics.ReservePlainSelectMemory(bytes, _budget); return bytes; }
        catch (SqlExecutionException failure) when (failure.Diagnostics[0].Code == SqlExecutionException.StatementMemoryBudgetCode)
        { return 0; } // At most one 8 KiB writer and one capped 16 KiB encoding beyond the grant.
    }
    private void Refuse() => throw SqlExecutionException.StatementMemoryBudget(_budget);
    private void DisposeAfterFailure(Exception failure)
    {
        try { Dispose(); }
        catch (SqlExecutionException cleanup) { failure.Data[nameof(SqlOutputSpool)] = cleanup; }
    }
    public void Dispose()
    {
        try { if (!_transferred) { if (_shared) { _run.Dispose(); } else { _statement.Dispose(); } } }
        catch (StorageTempException failure) { throw SqlSpilledResultSet.Translate(failure); }
        catch (AggregateException failure) { throw SqlSpilledResultSet.TranslateCleanup(failure); }
        finally { Release(); }
    }
    private sealed class DebugView { public DebugView(SqlOutputSpool value) { } }
}

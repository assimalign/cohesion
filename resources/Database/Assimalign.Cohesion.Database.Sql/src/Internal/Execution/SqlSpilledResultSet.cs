using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;

using Assimalign.Cohesion.Database.Execution;
using Assimalign.Cohesion.Database.Language;
using Assimalign.Cohesion.Database.Storage;
using Assimalign.Cohesion.Database.Types;

namespace Assimalign.Cohesion.Database.Sql.Internal;

/// <summary>Fully evaluated root outputs; only byte merging and decoding remain during delivery.</summary>
[System.Diagnostics.DebuggerDisplay("SQL spilled result")]
[System.Diagnostics.DebuggerTypeProxy(typeof(SqlSpilledResultSet.DebugView))]
internal sealed class SqlSpilledResultSet : DatabaseResultSet
{
    private readonly object _gate = new();
    private readonly IReadOnlyList<DatabaseColumn> _columns;
    private readonly StorageTempStatement _statement;
    private readonly SqlDatabaseSession _owner;
    private readonly HashSet<Cursor> _readers = [];
    private StorageTempSortReader? _sorted;
    private StorageTempRun? _distinct;
    private readonly long _offset;
    private readonly long _limit;
    private bool _disposed;
    internal Location? Location { get; set; }
    internal void CaptureMetrics(SqlStatementMetrics metrics) => SqlSortSpillMetrics.Refresh(metrics, _statement);
    internal void CheckOwner()
    {
        lock (_gate) { ObjectDisposedException.ThrowIf(_disposed || _owner.State == SessionState.Closed || _owner.Database.Engine.State == EngineState.Disposed, this); }
    }

    internal SqlSpilledResultSet(IReadOnlyList<DatabaseColumn> columns, StorageTempStatement statement,
        StorageTempSortReader sorted, long offset, long limit, SqlDatabaseSession owner)
    {
        _columns = columns; _statement = statement; _sorted = sorted; _offset = offset; _limit = limit; _owner = owner;
        try { SqlSpillLifetime.Attach(owner, this); }
        catch (Exception exception) { DisposeAfterFailure(exception); throw; }
    }
    internal SqlSpilledResultSet(IReadOnlyList<DatabaseColumn> columns, StorageTempStatement statement,
        StorageTempRun run, long offset, long limit, SqlDatabaseSession owner)
    {
        _columns = columns; _statement = statement; _distinct = run; _offset = offset; _limit = limit; _owner = owner;
        try { SqlSpillLifetime.Attach(owner, this); }
        catch (Exception exception) { DisposeAfterFailure(exception); throw; }
    }
    public override DatabaseResultStatus Status => DatabaseResultStatus.Success;
    public override long AffectedCount => -1;
    public override IReadOnlyList<Diagnostic> Diagnostics => [];
    public override IReadOnlyList<DatabaseColumn> Columns => _columns;

    internal static SqlExecutionException Translate(StorageTempException exception, Location? location = null)
    {
        var failure = new SqlExecutionException(exception.Code, exception.Message, location: location);
        // Temporary I/O must never enter the durable-storage failure walker. Retain its exact
        // cause as exception data, outside InnerException's durable-failure classification.
        failure.Data[nameof(StorageTempException)] = exception;
        return failure;
    }
    internal static SqlExecutionException TranslateCleanup(AggregateException exception, Location? location = null)
    {
        var cause = exception.Flatten().InnerExceptions.OfType<StorageTempException>().FirstOrDefault();
        var failure = new SqlExecutionException(cause?.Code ?? StorageTempException.UnavailableCode,
            cause?.Message ?? exception.Message, location: location);
        failure.Data[nameof(StorageTempException)] = exception;
        return failure;
    }
    internal static void ReleaseAfterFailure(StorageTempStatement statement, Exception failure)
    {
        try { statement.Dispose(); }
        catch (AggregateException cleanup) { failure.Data[nameof(StorageTempStatement)] = TranslateCleanup(cleanup); }
    }

    internal void Deduplicate(IEqualityComparer<object?[]> comparer, SqlStatementMetrics metrics, long budget, CancellationToken token)
    {
        SqlSelectMemory seenMemory = default;
        long scratch = 0;
        try
        {
            var seen = new HashSet<object?[]>(comparer);
            var original = _distinct;
            long readerBytes = _sorted?.BufferBytes ?? StorageTempSpace.PageSize + (long)original!.MaximumRecordLength;
            using var reader = _sorted is not null ? new Cursor(_sorted.OpenReader()) : new Cursor(original!);
            metrics.ReservePlainSelectMemory(readerBytes + StorageTempSpace.PageSize, budget);
            scratch = readerBytes + StorageTempSpace.PageSize;
            _distinct = _statement.CreateRun(token);
            while (reader.MoveNext(token, out var encoded))
            {
                var row = Decode(encoded.Span);
                if (seen.Contains(row)) { continue; }
                long bytes = seenMemory.RowBytes(row, out bool wide, overhead: 64);
                metrics.ReservePlainSelectMemory(bytes, budget); seenMemory.Bytes += bytes;
                if (wide) { seenMemory.AddRow(row); }
                seen.Add(row);
                // Reuse the encoded output bytes; no sort key or output materialization is retained.
                _distinct.Append(encoded.Span, token);
            }
            _distinct.Seal(token);
            _sorted?.Dispose(); _sorted = null; original?.Dispose();
        }
        catch (StorageTempException exception) { var failure = Translate(exception, Location); DisposeAfterFailure(failure); throw failure; }
        catch (Exception exception) { DisposeAfterFailure(exception); throw; }
        finally { metrics.ReleasePlainSelectMemory(seenMemory.Bytes + scratch); }
    }

    internal void Transform(Func<object?[], object?[]> projection, SqlStatementMetrics metrics, long budget, int bound, CancellationToken token)
    {
        long scratch = _sorted!.BufferBytes + StorageTempSpace.PageSize;
        metrics.ReservePlainSelectMemory(scratch, budget);
        try
        {
            _distinct = _statement.CreateRun(token);
            // This stage has not escaped to a consumer: consuming the owner releases each exhausted
            // input run as projection advances. Every volatile projection still precedes pagination.
            while (_sorted.MoveNext(token))
            {
                var output = projection(Decode(_sorted.Value.Span));
                if (!SqlTempRowCodec.TryGetLength(output, out int length) || length + 16L > bound || !SqlTempRowCodec.TryEncode(output, out var encoded))
                { throw new SqlExecutionException(SqlExecutionException.StatementMemoryBudgetCode,
                    string.Format(System.Globalization.CultureInfo.CurrentCulture, ErrorMessages.StatementMemoryBudgetExceeded, budget)); }
                _distinct.Append(encoded, token);
            }
            _distinct.Seal(token); _sorted.Dispose(); _sorted = null;
        }
        catch (StorageTempException exception) { var failure = Translate(exception, Location); DisposeAfterFailure(failure); throw failure; }
        catch (Exception exception) { DisposeAfterFailure(exception); throw; }
        finally { metrics.ReleasePlainSelectMemory(scratch); }
    }

    public override async IAsyncEnumerable<DatabaseRow> GetRowsAsync([EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        using var cursor = Open(cancellationToken);
        try
        {
            long skip = _offset, left = _limit;
            while (left != 0 && Read(cursor, cancellationToken, out var row))
            {
                if (skip > 0) { skip--; continue; }
                if (left > 0) { left--; }
                yield return new SqlMaterializedRow(row!);
            }
        }
        finally { lock (_gate) { _readers.Remove(cursor); } }
        await Task.CompletedTask.ConfigureAwait(false);
    }

    private Cursor Open(CancellationToken token)
    {
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            try
            {
                token.ThrowIfCancellationRequested();
                var cursor = _sorted is not null ? new Cursor(_sorted.OpenReader()) : new Cursor(_distinct!);
                _readers.Add(cursor); return cursor;
            }
            catch (Exception exception) { DisposeAfterFailure(exception); throw; }
        }
    }
    private bool Read(Cursor cursor, CancellationToken token, out object?[]? row)
    {
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            try
            {
                if (!cursor.MoveNext(token, out var bytes)) { row = null; return false; }
                row = Decode(bytes.Span); return true;
            }
            catch (StorageTempException exception) { var failure = Translate(exception, Location); DisposeAfterFailure(failure); throw failure; }
            catch (Exception exception) { DisposeAfterFailure(exception); throw; }
        }
    }
    private object?[] Decode(ReadOnlySpan<byte> value)
    {
        if (SqlTempRowCodec.TryDecode(value, out var row)) { return row; }
        throw new SqlExecutionException(StorageTempException.DamageCode, ErrorMessages.SpillRowDamaged, location: Location);
    }
    // Cleanup must preserve the initiating cancellation, memory refusal or read/projection error.
    internal void DisposeAfterFailure(Exception failure)
    {
        try { Dispose(); }
        catch (SqlExecutionException cleanup) { failure.Data[nameof(SqlSpilledResultSet)] = cleanup; }
    }
    public override ValueTask DisposeAsync() { Dispose(); return default; }
    internal void Dispose()
    {
        try { DisposeCore(); }
        catch (AggregateException exception) { throw TranslateCleanup(exception, Location); }
        catch (StorageTempException exception) { throw Translate(exception, Location); }
    }
    private void DisposeCore()
    {
        lock (_gate)
        {
            if (_disposed) { return; }
            _disposed = true;
            try
            {
                foreach (var reader in _readers) { reader.Dispose(); }
                _readers.Clear(); _sorted?.Dispose(); _sorted = null;
            }
            finally
            {
                try { _statement.Dispose(); }
                finally { SqlSpillLifetime.Detach(_owner, this); }
            }
        }
    }

    [System.Diagnostics.DebuggerDisplay("SQL spill cursor")]
    [System.Diagnostics.DebuggerTypeProxy(typeof(Cursor.DebugView))]
    private sealed class Cursor : IDisposable
    {
        private readonly StorageTempSortReader? _sorted;
        private readonly StorageTempRunReader? _reader;
        private byte[] _row = [];
        internal Cursor(StorageTempSortReader sorted) { _sorted = sorted; }
        internal Cursor(StorageTempRun run) { _reader = run.OpenReader(StorageTempSpace.PageSize); _row = new byte[run.MaximumRecordLength]; }
        internal bool MoveNext(CancellationToken token, out ReadOnlyMemory<byte> value)
        {
            if (_sorted is not null) { bool next = _sorted.MoveNext(token); value = _sorted.Value; return next; }
            bool found = _reader!.TryRead(_row, out int length, token); value = _row.AsMemory(0, length); return found;
        }
        public void Dispose() { _sorted?.Dispose(); _reader?.Dispose(); _row = []; }
        private sealed class DebugView { public DebugView(Cursor target) { } }
    }
    private sealed class DebugView { public DebugView(SqlSpilledResultSet target) { } }
}

using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.ExceptionServices;
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
internal sealed partial class SqlSpilledResultSet : DatabaseResultSet
{
    private readonly object _gate = new();
    private readonly IReadOnlyList<DatabaseColumn> _columns;
    private readonly StorageTempStatement _statement;
    private SqlDatabaseSession? _owner;
    private readonly long _readerGrant;
    private long _readerBytes;
    private long _maximumDecodedRowBytes;
    private long WorkspaceBytes => _sorted?.BufferBytes ?? StorageTempSpace.PageSize + (long)_distinct!.MaximumRecordLength;
    private ExceptionDispatchInfo? _failure;
    private readonly HashSet<Cursor> _readers = [];
    private StorageTempSortReader? _sorted;
    private StorageTempRun? _distinct;
    private readonly long _offset;
    private readonly long _limit;
    private bool _disposed;
    private bool _ownsStatement;
    internal Location? Location { get; set; }
    internal bool AdoptStatement(StorageTempStatement statement)
    {
        lock (_gate)
        {
            if (_disposed || !ReferenceEquals(_statement, statement)) { return false; }
            _ownsStatement = true; return true;
        }
    }
    internal void CaptureMetrics(SqlStatementMetrics metrics)
    {
        // Transform consumes the owning merge first. Once evaluation ends, discard its
        // unused template workspace through the existing fork lifecycle.
        if (_sorted is not null) { using var unused = _sorted.OpenReader(); }
        SqlSortSpillMetrics.Refresh(metrics, _statement);
    }
    internal void CheckOwner()
    {
        lock (_gate) { ObjectDisposedException.ThrowIf(_disposed || _owner!.State == SessionState.Closed || _owner.Database.Engine.State == EngineState.Disposed, this); }
    }

    internal SqlSpilledResultSet(IReadOnlyList<DatabaseColumn> columns, StorageTempStatement statement,
        StorageTempSortReader sorted, long offset, long limit, SqlDatabaseSession owner, long readerGrant, long maximumDecodedRowBytes,
        bool ownsStatement = true)
    {
        _columns = columns; _statement = statement; _sorted = sorted; _offset = offset; _limit = limit; _owner = owner; _readerGrant = readerGrant;
        _maximumDecodedRowBytes = maximumDecodedRowBytes;
        _ownsStatement = ownsStatement;
        try { ValidateFirstReader(); SqlSpillLifetime.Attach(owner, this); }
        catch (Exception exception) { DisposeAfterFailure(exception); throw; }
    }
    internal SqlSpilledResultSet(IReadOnlyList<DatabaseColumn> columns, StorageTempStatement statement,
        StorageTempRun run, long offset, long limit, SqlDatabaseSession owner, long readerGrant, long maximumDecodedRowBytes,
        bool ownsStatement = true)
    {
        _columns = columns; _statement = statement; _distinct = run; _offset = offset; _limit = limit; _owner = owner; _readerGrant = readerGrant;
        _maximumDecodedRowBytes = maximumDecodedRowBytes;
        _ownsStatement = ownsStatement;
        try { ValidateFirstReader(); SqlSpillLifetime.Attach(owner, this); }
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
            SqlRowCollector.ObserveSpill?.Invoke("deduplicate");
            var seen = new HashSet<object?[]>(comparer);
            var original = _distinct;
            long readerBytes = _sorted?.BufferBytes ?? StorageTempSpace.PageSize + (long)original!.MaximumRecordLength;
            using var reader = _sorted is not null ? new Cursor(_sorted.OpenReader()) : new Cursor(original!);
            metrics.ReservePlainSelectMemory(readerBytes + StorageTempSpace.PageSize, budget);
            scratch = readerBytes + StorageTempSpace.PageSize;
            _distinct = _statement.CreateRun(token);
            int visited = 0;
            while (reader.MoveNext(token, out var encoded))
            {
                if ((visited++ & 255) == 0) { SqlRowCollector.ObserveSpill?.Invoke("deduplicate"); }
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
        catch (AggregateException exception) { var failure = TranslateCleanup(exception, Location); DisposeAfterFailure(failure); throw failure; }
        catch (Exception exception) { DisposeAfterFailure(exception); throw; }
        finally { metrics.ReleasePlainSelectMemory(seenMemory.Bytes + scratch); }
    }

    internal void Transform(Func<object?[], object?[]> projection, SqlStatementMetrics metrics, long budget, int bound, CancellationToken token)
    {
        long scratch = _sorted!.BufferBytes + StorageTempSpace.PageSize;
        long maximumDecodedRowBytes = 0;
        metrics.ReservePlainSelectMemory(scratch, budget);
        try
        {
            _distinct = _statement.CreateRun(token);
            SqlRowCollector.ObserveSpill?.Invoke("transform");
            // This stage has not escaped to a consumer: consuming the owner releases each exhausted
            // input run as projection advances. Every volatile projection still precedes pagination.
            int visited = 0;
            while (_sorted.MoveNext(token))
            {
                var output = projection(Decode(_sorted.Value.Span));
                if ((visited++ & 255) == 0) { SqlRowCollector.ObserveSpill?.Invoke("transform"); }
                if (!SqlTempRowCodec.TryGetLength(output, out int length) || length + 16L > bound || !SqlTempRowCodec.TryEncode(output, out var encoded))
                { throw SqlExecutionException.StatementMemoryBudget(budget); }
                maximumDecodedRowBytes = Math.Max(maximumDecodedRowBytes, SqlTempRowCodec.DecodedBytes(output));
                _distinct.Append(encoded, token);
            }
            _distinct.Seal(token); _sorted.Dispose(); _sorted = null;
            _maximumDecodedRowBytes = maximumDecodedRowBytes; ValidateFirstReader();
        }
        catch (StorageTempException exception) { var failure = Translate(exception, Location); DisposeAfterFailure(failure); throw failure; }
        catch (AggregateException exception) { var failure = TranslateCleanup(exception, Location); DisposeAfterFailure(failure); throw failure; }
        catch (Exception exception) { DisposeAfterFailure(exception); throw; }
        finally { metrics.ReleasePlainSelectMemory(scratch); }
    }

    public override IAsyncEnumerable<DatabaseRow> GetRowsAsync(CancellationToken cancellationToken = default)
        => new Rows(this, cancellationToken);

    private void ValidateFirstReader()
    {
        // One retained row per cursor and one old/new decode overlap. Refuse before
        // publication if the first reader cannot fit the actual decoded row size.
        if (WorkspaceBytes + 2 * _maximumDecodedRowBytes > _readerGrant)
        { throw SqlExecutionException.StatementMemoryBudget(_readerGrant); }
    }

    private Cursor Open(CancellationToken token)
    {
        lock (_gate)
        {
            Check();
            try
            {
                token.ThrowIfCancellationRequested();
                long bytes = WorkspaceBytes + _maximumDecodedRowBytes;
                if (bytes > _readerGrant - _maximumDecodedRowBytes - _readerBytes) { throw SqlExecutionException.ReaderMemoryBudget(_readerGrant, _readerBytes + _maximumDecodedRowBytes, bytes).At(Location); }
                var cursor = _sorted is not null ? new Cursor(_sorted.OpenReader(), bytes) : new Cursor(_distinct!, bytes);
                _readers.Add(cursor); _readerBytes += bytes; return cursor;
            }
            catch (OperationCanceledException) { throw; }
            catch (SqlExecutionException exception) when (exception.Diagnostics[0].Code == SqlExecutionException.StatementMemoryBudgetCode) { throw; }
            catch (StorageTempException exception) { var failure = Translate(exception, Location); Fail(failure); throw failure; }
            catch (Exception exception) { Fail(exception); throw; }
        }
    }
    private void Check()
    {
        if (_failure is not null) { _failure.Throw(); }
        ObjectDisposedException.ThrowIf(_disposed, this);
    }
    private void Fail(Exception failure) { _failure ??= ExceptionDispatchInfo.Capture(failure); DisposeAfterFailure(failure); }
    private bool Read(Cursor cursor, CancellationToken token, out object?[]? row)
    {
        lock (_gate)
        {
            Check();
            try
            {
                if (!cursor.MoveNext(token, out var bytes)) { row = null; return false; }
                row = Decode(bytes.Span); return true;
            }
            catch (OperationCanceledException) { throw; }
            catch (StorageTempException exception) { var failure = Translate(exception, Location); Fail(failure); throw failure; }
            catch (Exception exception) { Fail(exception); throw; }
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
    public override ValueTask DisposeAsync()
    {
        try { Dispose(); return default; }
        catch (Exception exception) { return ValueTask.FromException(exception); }
    }
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
                _readers.Clear(); _readerBytes = 0; _sorted?.Dispose(); _sorted = null;
            }
            finally
            {
                try { if (_ownsStatement) { _statement.Dispose(); } else { _distinct?.Dispose(); } }
                finally { if (_owner is { } owner) { _owner = null; SqlSpillLifetime.Detach(owner, this); } _distinct = null; }
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
        internal DatabaseRow? Current { get; set; }
        internal long ReservedBytes { get; }
        internal Cursor(StorageTempSortReader sorted, long bytes = 0) { _sorted = sorted; ReservedBytes = bytes; }
        internal Cursor(StorageTempRun run, long bytes = 0) { _reader = run.OpenReader(StorageTempSpace.PageSize); _row = new byte[run.MaximumRecordLength]; ReservedBytes = bytes; }
        internal bool MoveNext(CancellationToken token, out ReadOnlyMemory<byte> value)
        {
            if (_sorted is not null) { bool next = _sorted.MoveNext(token); value = _sorted.Value; return next; }
            bool found = _reader!.TryRead(_row, out int length, token); value = _row.AsMemory(0, length); return found;
        }
        public void Dispose() { Current = null; _sorted?.Dispose(); _reader?.Dispose(); _row = []; }
        private sealed class DebugView { public DebugView(Cursor target) { } }
    }
    private sealed class DebugView { public DebugView(SqlSpilledResultSet target) { } }
}

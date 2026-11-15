using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using System.Runtime.ExceptionServices;
using System.Threading;
using System.Threading.Tasks;

using Assimalign.Cohesion.Database.Execution;
using Assimalign.Cohesion.Database.Storage;
using Assimalign.Cohesion.Database.Types;

namespace Assimalign.Cohesion.Database.Sql.Internal;

// An internal stage handoff, never a client result or a relation cache. Only the
// refused input owner creates one; its runs share the outer statement's quota.
[System.Diagnostics.DebuggerDisplay("SQL window input stage")]
[System.Diagnostics.DebuggerTypeProxy(typeof(DebugView))]
internal sealed class SqlWindowInputSpool : DatabaseResultSet, IDisposable
{
    private static ConditionalWeakTable<List<object?[]>, SqlWindowInputSpool>? _inputs;
    private static ConditionalWeakTable<SqlWindowMemory, SqlExecutionException>? _refusals;
    private readonly SqlStatementMetrics _metrics;
    private readonly long _budget;
    private readonly StorageTempRun? _run;
    private readonly StorageTempSortReader? _sorted;
    private readonly StorageTempStatement _statement;
    private byte[]? _scratch;
    private long _maximumDecoded;
    private bool _sealed;
    private IReadOnlyList<DatabaseColumn> _columns = [];
    internal const int MaximumRecordBytes = 16384;
    internal long Count { get; private set; }
    internal SqlExecutionException? Refusal { get; set; }
    internal StorageTempStatement Statement => _statement;

    internal SqlWindowInputSpool(SqlStatementMetrics metrics, long budget, SqlDatabaseSession owner, CancellationToken token)
    {
        _metrics = metrics; _budget = budget;
        _statement = SqlHashSpillMetrics.JoinTemp(metrics, owner);
        _run = _statement.CreateRun(token);
    }

    internal SqlWindowInputSpool(SqlStatementMetrics metrics, long budget, StorageTempStatement statement,
        StorageTempSortReader sorted, long maximumDecoded, long count)
    {
        _metrics = metrics; _budget = budget; _statement = statement; _sorted = sorted;
        _maximumDecoded = maximumDecoded; Count = count; _sealed = true;
    }

    internal SqlWindowInputSpool(SqlStatementMetrics metrics, long budget, StorageTempStatement statement,
        StorageTempRun run, long maximumDecoded, long count, SqlExecutionException refusal)
    {
        _metrics = metrics; _budget = budget; _statement = statement; _run = run;
        _maximumDecoded = maximumDecoded; Count = count; _sealed = true; Refusal = refusal;
    }

    internal static SqlWindowInputSpool? For(List<object?[]> rows)
        => _inputs?.TryGetValue(rows, out var input) == true ? input : null;

    // Keep the fitting admission exactly where it was. Retain is atomic, so a
    // refused row owns no partial references when the handoff drains the list.
    internal static bool Retain(List<object?[]> rows, object?[] row, SqlWindowMemory? memory, SqlStatementMetrics? metrics,
        long budget, SqlDatabaseSession? owner, CancellationToken token, ref SqlWindowInputSpool? input)
    {
        if (memory is null) { return false; }
        if (input is not null) { input.Append(row, token); return true; }
        try { memory.Retain(row); return false; }
        catch (SqlExecutionException failure) when (owner is not null && IsMemoryRefusal(failure))
        {
            RecordRefusal(memory, failure);
            SqlWindowInputSpool? spool = null;
            try
            {
                spool = new SqlWindowInputSpool(metrics!, budget, owner, token) { Refusal = RefusalFor(memory) };
                foreach (var buffered in rows)
                { spool.Append(buffered, token); SqlRowCollector.ObserveBuffer?.Invoke("window-drain", buffered); memory.ReleaseRow(buffered); }
                rows.Clear(); rows.Capacity = 0;
                spool.Append(row, token);
                LazyInitializer.EnsureInitialized(ref _inputs).Add(rows, spool);
                input = spool;
                return true;
            }
            catch (StorageTempException temp) when (temp.Code is StorageTempException.EngineQuotaCode or StorageTempException.StatementQuotaCode or StorageTempException.FreeSpaceCode)
            { spool?.Dispose(); failure.Data[nameof(SqlWindowInputSpool)] = temp; ExceptionDispatchInfo.Throw(failure); throw; }
            catch { spool?.Dispose(); throw; }
        }
    }

    internal static bool IsMemoryRefusal(SqlExecutionException failure)
        => failure.Diagnostics[0].Code is "CS05079" or SqlExecutionException.StatementMemoryBudgetCode;
    internal static SqlExecutionException? RefusalFor(SqlWindowMemory memory)
        => _refusals?.TryGetValue(memory, out var failure) == true ? failure : null;
    internal static void RecordRefusal(SqlWindowMemory memory, SqlExecutionException failure)
        => LazyInitializer.EnsureInitialized(ref _refusals).GetValue(memory, _ => failure);
    internal static void ForgetRefusal(SqlWindowMemory memory) => _refusals?.Remove(memory);

    internal static SqlWindowInputSpool Take(List<object?[]> rows, IReadOnlyList<DatabaseColumn> columns)
    {
        var input = For(rows)!; _inputs!.Remove(rows); input._columns = columns; return input;
    }

    internal static void Release(List<object?[]> rows)
    {
        if (For(rows) is not { } input) { return; }
        _inputs!.Remove(rows); input.Dispose();
    }

    internal static void Release(List<object?[]> rows, SqlWindowInputSpool? input)
    {
        if (input is null) { return; }
        _inputs!.Remove(rows); input.Dispose();
    }

    internal void Append(object?[] row, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        _scratch ??= new byte[MaximumRecordBytes];
        if (!SqlTempRowCodec.TryWrite(row, _scratch, out int size))
        {
            if (!SqlTempRowCodec.TryGetLength(row, out _)) { throw EncodingRefusal(_budget); }
            throw SqlExecutionException.StatementMemoryBudget(_budget);
        }
        long decoded = SqlTempRowCodec.DecodedBytes(row);
        if (StorageTempSpace.PageSize + size + 256L + 2 * decoded > _budget)
        { throw SqlExecutionException.StatementMemoryBudget(_budget); }
        _run!.Append(_scratch.AsSpan(0, size), token);
        _maximumDecoded = Math.Max(_maximumDecoded, decoded); Count++;
    }

    internal void Seal(CancellationToken token)
    {
        if (_sealed) { return; }
        _run!.Seal(token); _sealed = true;
        SqlHashSpillMetrics.RefreshJoin(_metrics, _statement);
        SqlSortSpillMetrics.RecordOutput(_metrics, _statement);
        SqlRowCollector.ObserveSpill?.Invoke("window-input");
    }

    internal long ReadBytes => (_sorted?.BufferBytes ?? StorageTempSpace.PageSize + _run!.MaximumRecordLength) + 256L + 2 * _maximumDecoded;

    internal IEnumerable<object?[]> Read(CancellationToken token)
    {
        Seal(token);
        long readBytes = ReadBytes;
        _metrics.ReservePlainSelectMemory(readBytes, _budget);
        try
        {
            if (_sorted is not null)
            {
                while (_sorted.MoveNext(token)) { yield return Decode(_sorted.Value.Span); }
            }
            else
            {
                using var reader = _run!.OpenReader(StorageTempSpace.PageSize);
                var buffer = new byte[_run.MaximumRecordLength];
                while (reader.TryRead(buffer, out int size, token)) { yield return Decode(buffer.AsSpan(0, size)); }
            }
        }
        finally
        {
            // A stage run is consumed once. Its merge workspace must die while its
            // reservation is still held, before a successor sort or DISTINCT reserves.
            try { Dispose(); }
            finally { _metrics.ReleasePlainSelectMemory(readBytes); }
        }
    }

    private static object?[] Decode(ReadOnlySpan<byte> encoded)
    {
        if (!SqlTempRowCodec.TryDecode(encoded, out var row))
        { throw new SqlExecutionException(StorageTempException.DamageCode, ErrorMessages.SpillRowDamaged); }
        SqlRowCollector.ObserveBuffer?.Invoke("window-decoded", row);
        return row;
    }

    internal static SqlExecutionException EncodingRefusal(long budget)
    {
        var failure = SqlExecutionException.StatementMemoryBudget(budget);
        failure.Data[nameof(SqlTempRowCodec)] = true; return failure;
    }

    // Storage counts a failed close and attempts every release. A consumed stage
    // cannot turn an already computed outcome into a cleanup failure.
    public void Dispose()
    {
        _scratch = null;
        try { _run?.Dispose(); _sorted?.Dispose(); }
        catch (AggregateException) { }
        catch (StorageTempException) { }
    }
    public override DatabaseResultStatus Status => DatabaseResultStatus.Success;
    public override long AffectedCount => -1;
    public override IReadOnlyList<Diagnostic> Diagnostics => [];
    public override IReadOnlyList<DatabaseColumn> Columns => _columns;
    public override async IAsyncEnumerable<DatabaseRow> GetRowsAsync([EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        foreach (var row in Read(cancellationToken)) { yield return new SqlMaterializedRow(row); }
        await Task.CompletedTask.ConfigureAwait(false);
    }
    public override ValueTask DisposeAsync() { Dispose(); return default; }

    private sealed class DebugView
    {
        private readonly SqlWindowInputSpool _value;
        public DebugView(SqlWindowInputSpool value) { _value = value; }
        public DatabaseResultStatus Status => _value.Status;
        public long AffectedCount => _value.AffectedCount;
        public IReadOnlyList<DatabaseColumn> Columns => _value.Columns;
        public IReadOnlyList<Diagnostic> Diagnostics => _value.Diagnostics;
    }
}

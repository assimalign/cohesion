using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using System.Runtime.ExceptionServices;

using Assimalign.Cohesion.Database.Execution;
using Assimalign.Cohesion.Database.Storage;

namespace Assimalign.Cohesion.Database.Sql.Internal;

internal sealed partial class SqlRowCollector
{
    // Only a refused reservation creates spill state. Fitting collectors retain their exact layout.
    private static readonly ConditionalWeakTable<SqlRowCollector, Spill> _spills = new();
    private static readonly ConditionalWeakTable<SqlRowCollector, StorageTempStatement> _sharedStatements = new();
    internal void ShareSpillStatement(StorageTempStatement statement) => _sharedStatements.Add(this, statement);
    internal static Action<string>? ObserveSpill { get; set; }
    internal static Action<string, object>? ObserveBuffer { get; set; }
    internal bool HasSpilled => _spills.TryGetValue(this, out _);

    private bool AdmitEntry(Entry entry, SqlDatabaseSession? owner)
    {
        try { ReserveEntry(entry); return true; }
        catch (SqlExecutionException exception) when (exception.Diagnostics[0].Code == SqlExecutionException.StatementMemoryBudgetCode)
        { if (TrySpill(entry, owner)) { return false; } throw; }
    }

    private bool TrySpill(Entry pending, SqlDatabaseSession? owner)
    {
        if (owner?.Database.Engine.TempSpace is not { } space || _orderBy.Count == 0) { return false; }
        foreach (var order in _orderBy) { if (!order.Collation.IsFixed) { return false; } }
        long grant = Math.Min(int.MaxValue, _budget - (_metrics!.HashMemoryBytes - _memory.Bytes));
        long bound = (grant - 32768) / 16 - 8192;
        if (grant < 65536 || bound < 16) { return false; }
        var columns = new SqlTempSortKeyCodec.Column[_orderBy.Count];
        for (int i = 0; i < columns.Length; i++)
        { columns[i] = new(_orderBy[i].IsDescending, _orderBy[i].NullsFirst, _collations[i]); }
        SqlSpillLifetime.CheckCapacity(owner);
        var state = new Spill(owner, space, columns, (int)bound, grant,
            _sharedStatements.TryGetValue(this, out var shared) ? shared : null);
        try
        {
            // Validate buffered rows as well as the refused row, before comparison or handoff.
            foreach (var entry in _ordered) { state.Validate(entry.Output, entry.Keys, _budget); }
            state.Validate(pending.Output, pending.Keys, _budget);
            try { _ordered.Sort(Compare); }
            catch (InvalidOperationException exception) when (exception.InnerException is DatabaseException or OperationCanceledException)
            { ExceptionDispatchInfo.Throw(exception.InnerException); throw; }
            // Scan/join iterators return detached rows after releasing their page latch. Order:
            // SQL object intent locks -> no page latch -> temp run lock -> probe gate -> space gate.
            // No temp call or user callback runs while a storage page latch is held.
            ObserveSpill?.Invoke("handoff");
            state.Sorter = StorageTempSorter.CreateWithSortedPrefix(state.Statement, grant, (int)bound, Drain(), 64, _cancellationToken);
            Reserve(grant); // the arena uses the statement's remaining grant, on the plain ledger
            _spills.Add(this, state);
            ObserveSpill?.Invoke("arena");
            AppendSpilled(state, pending.Output, pending.Keys);
            return true;
        }
        catch (StorageTempException exception) { var failure = SqlSpilledResultSet.Translate(exception); RevokeSpill(state, failure); throw failure; }
        catch (AggregateException exception) { var failure = SqlSpilledResultSet.TranslateCleanup(exception); RevokeSpill(state, failure); throw failure; }
        catch (Exception exception) { RevokeSpill(state, exception); throw; }

        IEnumerable<(ReadOnlyMemory<byte> Key, ReadOnlyMemory<byte> Value)> Drain()
        {
            for (int i = 0; i < _ordered.Count; i++)
            {
                _cancellationToken.ThrowIfCancellationRequested();
                var entry = _ordered[i];
                var encoded = state.Encode(entry.Output, entry.Keys, _budget);
                _ordered[i] = default;
                long bytes = _memory.RemoveRow(entry.Output) + _memory.RemoveRow(entry.Keys, 0);
                Release(bytes);
                entry = default; // the prefix iterator must not root a drained row across its yield
                yield return encoded;
            }
            _ordered.Clear(); _ordered.Capacity = 0;
            _memory.Clear();
            ObserveSpill?.Invoke("drained");
        }
    }

    private void AppendSpilled(Spill state, object?[] output, object?[] keys)
    {
        try
        {
            var encoded = state.Encode(output, keys, _budget);
            ObserveSpill?.Invoke("append");
            state.Sorter!.Append(encoded.Key.Span, encoded.Value.Span, _cancellationToken);
        }
        catch (StorageTempException exception) { var failure = SqlSpilledResultSet.Translate(exception); RevokeSpill(state, failure); throw failure; }
        catch (AggregateException exception) { var failure = SqlSpilledResultSet.TranslateCleanup(exception); RevokeSpill(state, failure); throw failure; }
        catch (Exception exception) { RevokeSpill(state, exception); throw; }
    }

    private void RevokeSpill(Spill state, Exception failure)
    {
        _spills.Remove(this);
        try { state.Dispose(); }
        catch (SqlExecutionException cleanup) { failure.Data[nameof(SqlRowCollector)] = cleanup; }
        finally { _metrics?.ReleasePlainSelectMemory(_memory.Bytes); _memory.Clear(); _ordered.Clear(); _ordered.Capacity = 0; }
    }

    internal SqlSpilledResultSet TakeSpilled(IReadOnlyList<DatabaseColumn> columns, long offset, long limit)
    {
        var state = _spills.GetValue(this, _ => throw new InvalidOperationException());
        try
        {
            ObserveSpill?.Invoke("complete");
            var reader = state.Sorter!.Complete(_cancellationToken);
            SqlSortSpillMetrics.Record(_metrics!, state.Statement, state.Sorter);
            state.Sorter.Dispose(); state.Sorter = null;
            Release(_memory.Bytes);
            var result = new SqlSpilledResultSet(columns, state.Statement, reader, offset, limit, state.Owner, state.Grant, state.MaximumDecodedRowBytes);
            _spills.Remove(this);
            return result;
        }
        catch (StorageTempException exception) { var failure = SqlSpilledResultSet.Translate(exception); RevokeSpill(state, failure); throw failure; }
        catch (AggregateException exception) { var failure = SqlSpilledResultSet.TranslateCleanup(exception); RevokeSpill(state, failure); throw failure; }
        catch (Exception exception) { RevokeSpill(state, exception); throw; }
    }

    [System.Diagnostics.DebuggerDisplay("SQL sort spill")]
    [System.Diagnostics.DebuggerTypeProxy(typeof(Spill.DebugView))]
    private sealed class Spill : IDisposable
    {
        internal readonly SqlDatabaseSession Owner;
        internal readonly StorageTempStatement Statement;
        internal readonly SqlTempSortKeyCodec.Column[] Columns;
        internal readonly byte[] Families;
        internal readonly int Bound;
        internal readonly long Grant;
        internal long MaximumDecodedRowBytes;
        internal StorageTempSorter? Sorter;
        internal Spill(SqlDatabaseSession owner, StorageTempSpace space, SqlTempSortKeyCodec.Column[] columns, int bound, long grant, StorageTempStatement? shared)
        {
            Owner = owner; Grant = grant; Columns = columns; Families = new byte[columns.Length]; Bound = bound;
            Statement = shared ?? space.CreateStatement(owner.Database.Engine.StatementTempSpaceQuota);
        }
        internal void Validate(object?[] output, object?[] keys, long budget)
        {
            if (!SqlTempSortKeyCodec.TryEncode(keys, Columns, Families, out var key)
                || !SqlTempRowCodec.TryGetLength(output, out int length) || 16L + key.Length + length > Bound)
            { throw SqlExecutionException.StatementMemoryBudget(budget); }
        }
        internal (ReadOnlyMemory<byte> Key, ReadOnlyMemory<byte> Value) Encode(object?[] output, object?[] keys, long budget)
        {
            if (!SqlTempSortKeyCodec.TryEncode(keys, Columns, Families, out var key)
                || !SqlTempRowCodec.TryGetLength(output, out int length) || 16L + key.Length + length > Bound
                || !SqlTempRowCodec.TryEncode(output, out var value))
            { throw SqlExecutionException.StatementMemoryBudget(budget); }
            MaximumDecodedRowBytes = Math.Max(MaximumDecodedRowBytes, SqlTempRowCodec.DecodedBytes(output));
            return (key, value);
        }
        public void Dispose()
        {
            try { try { Sorter?.Dispose(); } finally { Statement.Dispose(); } }
            catch (AggregateException exception) { throw SqlSpilledResultSet.TranslateCleanup(exception); }
        }
        private sealed class DebugView { public DebugView(Spill target) { } }
    }
}

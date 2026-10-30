using System;
using System.Collections.Generic;
using System.Runtime.ExceptionServices;
using System.Threading;

using Assimalign.Cohesion.Database.Types;

namespace Assimalign.Cohesion.Database.Sql.Internal;

/// <summary>Retains completed outputs and keys, bounded by OFFSET + LIMIT when ordered.</summary>
[global::System.Diagnostics.DebuggerDisplay("SqlRowCollector")]
[global::System.Diagnostics.DebuggerTypeProxy(typeof(global::Assimalign.Cohesion.Database.Sql.Internal.SqlRowCollector.DebugView))]
internal sealed class SqlRowCollector
{
    private readonly IReadOnlyList<SqlBoundOrdering> _orderBy;
    private readonly Collation[] _collations;
    private readonly SqlSubqueryValues? _subqueries;
    private readonly bool _canBound;
    private readonly CancellationToken _cancellationToken;
    private readonly SqlStatementMetrics? _metrics;
    private readonly long? _limit;
    private readonly long _offset;
    private readonly long _bound;
    private readonly List<Entry> _ordered = [];
    private readonly List<object?[]> _rows = [];
    private long _visited;
    private int _comparisons;
    private bool _collationsInitialized;
    private bool _isHeap;

    internal SqlRowCollector(IReadOnlyList<SqlBoundOrdering> orderBy, SqlSubqueryValues? subqueries,
        SqlRowCount? limit, SqlRowCount? offset, CancellationToken cancellationToken, SqlStatementMetrics? metrics = null)
    {
        _orderBy = orderBy;
        _subqueries = subqueries;
        _cancellationToken = cancellationToken;
        _metrics = metrics;
        _limit = limit?.Count;
        _offset = offset?.Count ?? 0;
        // Do not size an allocation or narrow a count from user input. A sum beyond
        // Int64.MaxValue is indistinguishable from an unbounded materialized input.
        _bound = _limit is { } count && count <= long.MaxValue - _offset ? count + _offset : long.MaxValue;
        _collations = new Collation[orderBy.Count];
        _canBound = _limit.HasValue;
        // Deferred collations follow the last qualifying row, so retain full sorting.
        for (int i = 0; i < orderBy.Count; i++) { _canBound &= orderBy[i].Collation.IsFixed; }
        cancellationToken.ThrowIfCancellationRequested();
    }

    internal bool IsComplete => _limit == 0 || _orderBy.Count == 0 && _limit.HasValue && _visited >= _bound;

    internal int RetainedCount => _orderBy.Count == 0 ? _rows.Count : _ordered.Count;

    /// <summary>Discards an already projected unordered sequence row within OFFSET without retaining it.</summary>
    internal bool SkipSequenceOffset()
    {
        if (_orderBy.Count != 0 || _visited >= _offset) { return false; }
        _visited++;
        return true;
    }

    internal object?[]? Add(object?[] output, object?[] keys)
    {
        _cancellationToken.ThrowIfCancellationRequested();
        var windowMemory = SqlWindowMemory.For(_metrics);
        windowMemory?.Retain(output);
        if (keys.Length > 0) { windowMemory?.Retain(keys); }
        long ordinal = _visited++;
        if (_orderBy.Count == 0)
        {
            if (ordinal >= _offset) { _rows.Add(output); return null; }
            windowMemory?.ReleaseRow(output);
            return output;
        }
        var entry = new Entry(output, keys, ordinal);
        // Capture while this qualifying row's subqueries are still available. A later
        // rejected row can advance/clear the frame before Finish runs.
        ResolveCollations(refresh: !_canBound);
        if (!_canBound)
        {
            windowMemory?.Reserve(24); // stable-sort entry capacity and growth
            _ordered.Add(entry);
            RecordRetainedCount();
            return null;
        }
        // Max-heap: the worst retained key (latest input on ties) is at the root.
        // PostgreSQL src/backend/utils/sort/tuplesort.c:2482-2523 uses the same reversed-order bound;
        // this implementation keeps the stable input ordinal explicitly.
        if (_ordered.Count < _bound)
        {
            windowMemory?.Reserve(24); // stable-sort entry capacity and growth
            _ordered.Add(entry);
            RecordRetainedCount();
            return null;
        }
        if (!_isHeap)
        {
            // Build only when the bound is exceeded: LIMIT >= input pays for one sort,
            // without per-row sifting. Unlike tuplesort.c:1126-1144's 2x staging,
            // overflow-triggered heapification preserves an exact OFFSET + LIMIT bound.
            for (int parent = _ordered.Count / 2 - 1; parent >= 0; parent--)
            {
                SiftDown(parent, _ordered[parent]);
            }
            _isHeap = true;
        }
        if (_ordered.Count > 0 && Compare(entry, _ordered[0]) < 0)
        {
            var replaced = _ordered[0];
            SiftDown(0, entry);
            windowMemory?.ReleaseRow(replaced.Output);
            windowMemory?.ReleaseRow(replaced.Keys);
            return replaced.Output;
        }
        windowMemory?.ReleaseRow(output);
        windowMemory?.ReleaseRow(keys);
        return output;
    }

    internal List<object?[]> Finish(bool resolveEmptyCollations = true, bool includeOffset = false)
    {
        _cancellationToken.ThrowIfCancellationRequested();
        if (_orderBy.Count == 0) { return _rows; }
        if (_ordered.Count > 0 || resolveEmptyCollations) { ResolveCollations(); }
        try { _ordered.Sort(Compare); }
        catch (InvalidOperationException exception) when (exception.InnerException is DatabaseException or OperationCanceledException)
        {
            ExceptionDispatchInfo.Throw(exception.InnerException);
            throw;
        }
        _cancellationToken.ThrowIfCancellationRequested();
        int start = includeOffset ? 0 : (int)Math.Min(_offset, _ordered.Count);
        int count = includeOffset ? (int)Math.Min(_bound, _ordered.Count)
            : (int)Math.Min(_limit ?? _ordered.Count, _ordered.Count - start);
        for (int i = start; i < start + count; i++)
        {
            _cancellationToken.ThrowIfCancellationRequested();
            _rows.Add(_ordered[i].Output);
        }
        if (SqlWindowMemory.For(_metrics) is { } windowMemory)
        {
            for (int i = 0; i < _ordered.Count; i++)
            {
                windowMemory.ReleaseRow(_ordered[i].Keys);
                if (i < start || i >= start + count) { windowMemory.ReleaseRow(_ordered[i].Output); }
            }
        }
        _ordered.Clear();
        return _rows;
    }

    private void RecordRetainedCount()
    {
        if (_metrics is not null) { _metrics.PeakOrderedRows = Math.Max(_metrics.PeakOrderedRows, _ordered.Count); }
    }

    private void SiftDown(int parent, Entry entry)
    {
        while (parent < _ordered.Count / 2)
        {
            int child = parent * 2 + 1;
            if (child + 1 < _ordered.Count && Compare(_ordered[child], _ordered[child + 1]) < 0) { child++; }
            if (Compare(entry, _ordered[child]) >= 0) { break; }
            _ordered[parent] = _ordered[child];
            parent = child;
        }
        _ordered[parent] = entry;
    }

    private void ResolveCollations(bool refresh = false)
    {
        if (_collationsInitialized && !refresh) { return; }
        for (int i = 0; i < _orderBy.Count; i++)
        {
            _cancellationToken.ThrowIfCancellationRequested();
            if (!_collationsInitialized || !_orderBy[i].Collation.IsFixed)
            {
                _collations[i] = _orderBy[i].Collation.Resolve(_subqueries);
            }
        }
        _collationsInitialized = true;
    }

    private int Compare(Entry left, Entry right)
    {
        for (int i = 0; i < _orderBy.Count; i++)
        {
            if ((_comparisons++ & 63) == 0) { _cancellationToken.ThrowIfCancellationRequested(); }
            var ordering = _orderBy[i];
            var a = left.Keys[i];
            var b = right.Keys[i];
            int comparison = a is null ? b is null ? 0 : ordering.NullsFirst ? -1 : 1
                : b is null ? ordering.NullsFirst ? 1 : -1
                : (ordering.IsDescending ? -1 : 1) * Math.Sign(SqlValueComparer.Compare(a, b, _collations[i]));
            if (comparison != 0) { return comparison; }
        }
        return left.Ordinal.CompareTo(right.Ordinal);
    }

    private readonly record struct Entry(object?[] Output, object?[] Keys, long Ordinal);

    private sealed class DebugView
    {
        public DebugView(global::Assimalign.Cohesion.Database.Sql.Internal.SqlRowCollector value)
        {
            _ = value;
        }
    }
}

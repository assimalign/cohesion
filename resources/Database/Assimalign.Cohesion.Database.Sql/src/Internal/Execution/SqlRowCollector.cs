using System;
using System.Collections.Generic;
using System.Runtime.ExceptionServices;
using System.Threading;

using Assimalign.Cohesion.Database.Types;

namespace Assimalign.Cohesion.Database.Sql.Internal;

/// <summary>Retains completed outputs and keys, bounded by OFFSET + LIMIT when ordered.</summary>
internal sealed class SqlRowCollector
{
    private readonly IReadOnlyList<SqlBoundOrdering> _orderBy;
    private readonly Collation[] _collations;
    private readonly SqlSubqueryValues? _subqueries;
    private readonly bool _canBound;
    private readonly CancellationToken _cancellationToken;
    private readonly long? _limit;
    private readonly long _offset;
    private readonly long _bound;
    private readonly List<Entry> _ordered = [];
    private readonly List<object?[]> _rows = [];
    private long _visited;
    private int _comparisons;
    private bool _collationsInitialized;

    internal SqlRowCollector(IReadOnlyList<SqlBoundOrdering> orderBy, SqlSubqueryValues? subqueries,
        SqlRowCount? limit, SqlRowCount? offset, CancellationToken cancellationToken)
    {
        _orderBy = orderBy;
        _subqueries = subqueries;
        _cancellationToken = cancellationToken;
        _limit = limit?.Count;
        _offset = offset?.Count ?? 0;
        // Do not size an allocation or narrow a count from user input. A sum beyond
        // Int64.MaxValue is indistinguishable from an unbounded materialized input.
        _bound = _limit is { } count && count <= long.MaxValue - _offset ? count + _offset : long.MaxValue;
        _collations = new Collation[orderBy.Count];
        _canBound = _limit.HasValue;
        // A deferred collation may depend on the final correlated row's frame.
        // Preserve full-sort resolution for that uncommon case.
        for (int i = 0; i < orderBy.Count; i++) { _canBound &= orderBy[i].Collation.IsFixed; }
        cancellationToken.ThrowIfCancellationRequested();
    }

    internal bool IsComplete => _limit == 0 || _orderBy.Count == 0 && _limit.HasValue && _visited >= _bound;

    internal int RetainedCount => _orderBy.Count == 0 ? _rows.Count : _ordered.Count;

    internal void Add(object?[] output, object?[] keys)
    {
        _cancellationToken.ThrowIfCancellationRequested();
        long ordinal = _visited++;
        if (_orderBy.Count == 0)
        {
            if (ordinal >= _offset) { _rows.Add(output); }
            return;
        }
        var entry = new Entry(output, keys, ordinal);
        if (!_canBound)
        {
            _ordered.Add(entry);
            return;
        }
        // Max-heap: the worst retained key (latest input on ties) is at the root.
        // PostgreSQL src/backend/utils/sort/tuplesort.c:2482-2523 uses the same reversed-order bound;
        // this implementation keeps the stable input ordinal explicitly.
        ResolveCollations();
        if (_ordered.Count < _bound)
        {
            int child = _ordered.Count;
            _ordered.Add(entry);
            while (child > 0)
            {
                int parent = (child - 1) / 2;
                if (Compare(_ordered[parent], entry) >= 0) { break; }
                _ordered[child] = _ordered[parent];
                child = parent;
            }
            _ordered[child] = entry;
        }
        else if (_ordered.Count > 0 && Compare(entry, _ordered[0]) < 0)
        {
            int parent = 0;
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
    }

    internal List<object?[]> Finish()
    {
        _cancellationToken.ThrowIfCancellationRequested();
        if (_orderBy.Count == 0) { return _rows; }
        if (_ordered.Count > 0) { ResolveCollations(); }
        try { _ordered.Sort(Compare); }
        catch (InvalidOperationException exception) when (exception.InnerException is DatabaseException or OperationCanceledException)
        {
            ExceptionDispatchInfo.Throw(exception.InnerException);
            throw;
        }
        _cancellationToken.ThrowIfCancellationRequested();
        int start = (int)Math.Min(_offset, _ordered.Count);
        int count = (int)Math.Min(_limit ?? _ordered.Count, _ordered.Count - start);
        for (int i = start; i < start + count; i++)
        {
            _cancellationToken.ThrowIfCancellationRequested();
            _rows.Add(_ordered[i].Output);
        }
        _ordered.Clear();
        return _rows;
    }

    private void ResolveCollations()
    {
        if (_collationsInitialized) { return; }
        for (int i = 0; i < _orderBy.Count; i++)
        {
            _cancellationToken.ThrowIfCancellationRequested();
            _collations[i] = _orderBy[i].Collation.Resolve(_subqueries);
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
}

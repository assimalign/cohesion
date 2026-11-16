using System;
using System.Collections;
using System.Collections.Generic;

namespace Assimalign.Cohesion.Database.Sql.Internal;

// LINQ lends ICollection.CopyTo its own exact-sized tuple buffer. Intercepting that buffer
// avoids an additional growing key cache and preserves every already evaluated volatile key.
[System.Diagnostics.DebuggerDisplay("SQL evaluated sort buffer")]
[System.Diagnostics.DebuggerTypeProxy(typeof(SqlSortBufferAdapter.DebugView))]
internal sealed class SqlSortBufferAdapter : ICollection<(object?[] Row, object?[] Keys)>
{
    [ThreadStatic]
    private static SqlSortBufferAdapter? _available;
    private List<object?[]>? _rows;
    private SqlExpressionEvaluator? _evaluator;
    private IEnumerable<(object?[] Row, object?[] Keys)>? _source;
    private (object?[] Row, object?[] Keys)[]? _buffer;
    private int _offset;
    private int _index;

    internal SqlExpressionEvaluator Evaluator => _evaluator!;
    public int Count => _rows?.Count ?? 0;
    public bool IsReadOnly => true;
    internal static SqlSortBufferAdapter Rent(List<object?[]> rows, SqlExpressionEvaluator evaluator)
    {
        var adapter = _available ?? new SqlSortBufferAdapter(); _available = null;
        adapter._rows = rows; adapter._evaluator = evaluator; return adapter;
    }
    internal void SetSource(IEnumerable<(object?[] Row, object?[] Keys)> source) => _source = source;
    internal void Return()
    {
        _rows = null; _evaluator = null; _source = null; _buffer = null; _offset = _index = 0;
        _available ??= this; // A cached adapter owns no rows, evaluator or execution frame.
    }
    public void CopyTo((object?[] Row, object?[] Keys)[] array, int arrayIndex)
    {
        _buffer = array; _offset = arrayIndex; _index = 0;
        foreach (var item in _source!) { array[_offset + _index++] = item; }
        _buffer = null;
    }
    internal void Capture(object?[] row, object?[] keys, long scratchBytes, SqlExecutionException failure)
    {
        if (_buffer is null) { throw failure; }
        _buffer[_offset + _index] = (row, keys);
        throw new SqlSortBudgetHandoff(_buffer, _offset, _index + 1, scratchBytes, failure);
    }
    public IEnumerator<(object?[] Row, object?[] Keys)> GetEnumerator() => _source!.GetEnumerator();
    IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
    public void Add((object?[] Row, object?[] Keys) item) => throw new NotSupportedException(ErrorMessages.SortBufferReadOnly);
    public void Clear() => throw new NotSupportedException(ErrorMessages.SortBufferReadOnly);
    public bool Contains((object?[] Row, object?[] Keys) item) => throw new NotSupportedException(ErrorMessages.SortBufferReadOnly);
    public bool Remove((object?[] Row, object?[] Keys) item) => throw new NotSupportedException(ErrorMessages.SortBufferReadOnly);
    private sealed class DebugView { public DebugView(SqlSortBufferAdapter target) { } }
}

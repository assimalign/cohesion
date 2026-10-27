using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;

namespace Assimalign.Cohesion.Database.Graph.Internal;

/// <summary>Owns retained projections, evicting the worst row when an ordered page has a finite end.</summary>
[global::System.Diagnostics.DebuggerDisplay("GraphTopRows")]
[global::System.Diagnostics.DebuggerTypeProxy(typeof(global::Assimalign.Cohesion.Database.Graph.Internal.GraphTopRows.DebugView))]
internal sealed class GraphTopRows : IDisposable
{
    private readonly GraphMemoryOwner _memory;
    private readonly GraphSortPagePlan? _stage;
    private readonly CancellationToken _token;
    private readonly long _capacity;
    private readonly List<GraphOrderedRow> _rows = [];
    private readonly PriorityQueue<GraphOrderedRow, GraphOrderedRow>? _top;

    internal GraphTopRows(GraphQueryMemory memory, GraphSortPagePlan? stage, CancellationToken token)
    {
        _memory = new(memory);
        _stage = stage;
        _token = token;
        _capacity = stage?.Limit is { } limit
            ? limit > long.MaxValue - stage.Offset ? long.MaxValue : stage.Offset + limit : long.MaxValue;
        if (stage is { Keys.Count: > 0, Limit: not null })
        {
            _top = new(Comparer<GraphOrderedRow>.Create((left, right) =>
                GraphSortPage.Compare(right, left, stage.Keys, token)));
        }
    }

    internal void Add(GraphOrderedRow row)
    {
        _token.ThrowIfCancellationRequested();
        if (_top is not null && (_capacity == 0 || _top.Count >= _capacity &&
            GraphSortPage.Compare(row, _top.Peek(), _stage!.Keys, _token) >= 0)) { return; }
        GraphOrderedRow? evicted = null;
        if (_top is not null && _top.Count >= _capacity)
        {
            evicted = _top.Dequeue();
            _memory.ReleaseValue(evicted.Values); _memory.ReleaseValue(evicted.Keys); _memory.ReleaseValue(evicted.Scope);
            _memory.Release(evicted.RetainedBytes);
        }
        row.RetainedBytes = 112;
        _memory.Add(row.RetainedBytes);
        _memory.RetainValue(row.Values, _token); _memory.RetainValue(row.Keys, _token); _memory.RetainValue(row.Scope, _token);
        if (_top is not null) { _top.Enqueue(row, row); }
        else { _rows.Add(row); }
    }

    internal List<GraphOrderedRow> Rows()
    {
        if (_top is null) { return _rows; }
        _memory.Add(32 + 16L * _top.Count);
        return _top.UnorderedItems.Select(item => item.Element).ToList();
    }

    public void Dispose()
    {
        _top?.Clear();
        _rows.Clear();
        _memory.Dispose();
    }

    private sealed class DebugView
    {
        public DebugView(global::Assimalign.Cohesion.Database.Graph.Internal.GraphTopRows value)
        {
            _ = value;
        }
    }
}

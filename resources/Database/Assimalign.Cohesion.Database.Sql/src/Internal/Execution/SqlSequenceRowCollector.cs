using System;
using System.Collections.Generic;
using System.Threading;

namespace Assimalign.Cohesion.Database.Sql.Internal;

/// <summary>Collects ordered sequence inputs, charging bounded selection to the shared statement budget.</summary>
[global::System.Diagnostics.DebuggerDisplay("SqlSequenceRowCollector")]
[global::System.Diagnostics.DebuggerTypeProxy(typeof(DebugView))]
internal sealed class SqlSequenceRowCollector : IDisposable
{
    private readonly SqlRowCollector _rows;
    private readonly SqlStatementMetrics _metrics;
    private readonly long _budget;
    private readonly Dictionary<object?[], long>? _charges;
    private long _reserved;

    internal SqlSequenceRowCollector(IReadOnlyList<SqlBoundOrdering> orderBy, SqlSubqueryValues? subqueries,
        SqlRowCount? limit, SqlRowCount? offset, CancellationToken cancellationToken, SqlStatementMetrics metrics, long budget)
    {
        _rows = new(orderBy, subqueries, limit, offset, cancellationToken, metrics);
        _metrics = metrics;
        _budget = budget;
        // Unbounded sorting follows the sequence-free collector's uncharged accounting.
        if (_rows.IsBounded) { _charges = new(ReferenceEqualityComparer.Instance); }
    }

    internal bool IsComplete => _rows.IsComplete;
    internal int RetainedCount => _rows.RetainedCount;

    internal void Add(object?[] output, object?[] keys)
    {
        var replaced = _rows.Add(output, keys);
        if (_charges is null || ReferenceEquals(replaced, output)) { return; }
        if (replaced is not null)
        {
            long bytes = _charges[replaced];
            _charges.Remove(replaced);
            _metrics.ReleaseHashMemory(bytes);
            _reserved -= bytes;
        }
        // Includes conservative list/entry/dictionary capacity overhead and both payloads.
        long charge = 192L + SqlHashJoinTable.RowBytes(output) + SqlHashJoinTable.RowBytes(keys);
        _metrics.ReserveHashMemory(charge, _budget);
        _reserved += charge;
        _charges.Add(output, charge);
    }

    internal List<object?[]> Finish() => _rows.Finish(includeOffset: true);

    public void Dispose()
    {
        _metrics.ReleaseHashMemory(_reserved);
        _reserved = 0;
        _charges?.Clear();
    }

    private sealed class DebugView
    { public DebugView(SqlSequenceRowCollector value) { _ = value; } }
}

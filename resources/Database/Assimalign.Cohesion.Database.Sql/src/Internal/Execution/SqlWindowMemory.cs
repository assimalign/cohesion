using System;
using System.Globalization;
using System.Threading;

namespace Assimalign.Cohesion.Database.Sql.Internal;

/// <summary>Statement grant for the window input, sort indexes, keys and partition state.</summary>
[System.Diagnostics.DebuggerDisplay("SqlWindowMemory")]
[System.Diagnostics.DebuggerTypeProxy(typeof(DebugView))]
internal sealed class SqlWindowMemory : IDisposable
{
    private static readonly AsyncLocal<SqlWindowMemory?> _current = new();
    private readonly SqlWindowMemory? _previous;
    private readonly SqlStatementMetrics _metrics;
    private readonly long _budget;
    private long _reserved;

    internal SqlWindowMemory(SqlStatementMetrics metrics, long budget)
    {
        _metrics = metrics;
        _budget = budget;
        _previous = _current.Value;
        Reserve(512);
        _current.Value = this;
    }

    internal static SqlWindowMemory? Current => _current.Value;
    internal static SqlWindowMemory? For(SqlStatementMetrics? metrics)
        => Current is { } memory && ReferenceEquals(memory._metrics, metrics) ? memory : null;
    internal void Reserve(long bytes)
    {
        if (bytes > _budget - _metrics.HashMemoryBytes)
        { throw new SqlExecutionException("CS05079", string.Format(CultureInfo.CurrentCulture, ErrorMessages.WindowMemoryBudget, _budget)); }
        _metrics.ReserveHashMemory(bytes, _budget);
        _reserved += bytes;
    }
    internal void Release(long bytes) { _metrics.ReleaseHashMemory(bytes); _reserved -= bytes; }

    // Conservative ownership: aliased payloads can be charged more than once. Include
    // list capacity/growth and reference-array headers before any row enters a buffer.
    internal void Retain(object?[] row) => Reserve(64 + SqlHashJoinTable.RowBytes(row));

    public void Dispose()
    {
        _current.Value = _previous;
        _metrics.ReleaseHashMemory(_reserved);
        _reserved = 0;
    }

    private sealed class DebugView
    {
        public DebugView(SqlWindowMemory value) { _ = value; }
    }
}

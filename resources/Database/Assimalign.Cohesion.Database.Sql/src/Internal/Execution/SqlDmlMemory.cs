using System;
using System.Collections.Generic;

namespace Assimalign.Cohesion.Database.Sql.Internal;

/// <summary>Owns retained DML storage until its statement or closure attempt unwinds.</summary>
[global::System.Diagnostics.DebuggerDisplay("SqlDmlMemory")]
[global::System.Diagnostics.DebuggerTypeProxy(typeof(SqlDmlMemory.DebugView))]
internal class SqlDmlMemory : IDisposable
{
    private readonly SqlStatementMetrics _metrics;
    private readonly long _budget;
    private readonly Dictionary<object, Entry> _objects = new(ReferenceEqualityComparer.Instance);
    private long _reserved;
    private int _slots;

    internal SqlDmlMemory(SqlStatementMetrics metrics, long budget)
    {
        _metrics = metrics;
        _budget = budget;
        Reserve(320); // owner, promoted join work, dictionary and initial array headers
    }

    internal static SqlDmlMemory ForAttempt(SqlStatementMetrics metrics, long budget, SqlDmlMemory? retained)
        => new AttemptMemory(metrics, budget, retained);

    internal void Reserve(long bytes)
    {
        _metrics.ReserveHashMemory(bytes, _budget);
        _reserved += bytes;
    }

    internal void Release(long bytes)
    {
        _metrics.ReleaseHashMemory(bytes);
        _reserved -= bytes;
    }

    // Projected constraint arrays are exclusive to their index. Their cells alias
    // retained final rows, so only the array leaves the ledger; spare slots are reused.
    internal void ReleaseProjection(object?[] key)
    {
        if (_objects.Remove(key, out var entry))
        { Release(Math.Max(0, entry.Bytes - entry.HashBytes)); }
    }

    internal void Retain(object?[] row)
    {
        Track(row, 24L + 8L * row.Length, retain: true, 0);
        foreach (var value in row)
        {
            if (value is not null) { Track(value, SqlHashJoinTable.ValueBytes(value), retain: true, 0); }
        }
    }

    internal void Retain(byte[] record) => Track(record, (24L + record.Length + 7) & ~7L, retain: true, 0);

    // Hash tables keep their original row/array charges. Credit any overlapping retained
    // objects before Add reserves the row, then restore ownership after Dispose releases it.
    internal void HashRow(object?[] row, bool adding)
    {
        long bytes = 24L + 8L * row.Length;
        Track(row, bytes, retain: false, adding ? bytes : -bytes);
        foreach (var value in row)
        {
            if (value is null) { continue; }
            bytes = SqlHashJoinTable.ValueBytes(value);
            Track(value, bytes, retain: false, adding ? bytes : -bytes);
        }
    }

    private protected virtual void Track(object value, long bytes, bool retain, long hashChange)
    {
        if (!_objects.TryGetValue(value, out var entry) && _objects.Count == _slots)
        {
            // Covers dictionary entries/buckets, including simultaneous old/new arrays at
            // growth. The ledger itself must never become an unaccounted retained buffer.
            Reserve(160);
            _slots++;
        }
        long previous = entry.Retained ? Math.Max(0, entry.Bytes - entry.HashBytes) : 0;
        entry = new(Math.Max(entry.Bytes, bytes), entry.HashBytes + hashChange, entry.Retained || retain);
        long current = entry.Retained ? Math.Max(0, entry.Bytes - entry.HashBytes) : 0;
        if (current > previous) { Reserve(current - previous); }
        else if (current < previous)
        {
            _metrics.ReleaseHashMemory(previous - current);
            _reserved -= previous - current;
        }
        if (!entry.Retained && entry.HashBytes == 0)
        {
            // Keep the capacity charge, but never pin an unselected hash payload after
            // its table releases the corresponding row reservation.
            _objects.Remove(value);
            return;
        }
        _objects[value] = entry;
    }

    public void Dispose()
    {
        _objects.Clear();
        _metrics.ReleaseHashMemory(_reserved);
        _reserved = 0;
    }

    private readonly record struct Entry(long Bytes, long HashBytes, bool Retained);

    // Attempts borrow objects already owned by the retained direct proposals. The
    // extra state is confined to this leaf, preserving ordinary DML's owner size.
    private sealed class AttemptMemory : SqlDmlMemory
    {
        private readonly SqlDmlMemory? _retained;

        internal AttemptMemory(SqlStatementMetrics metrics, long budget, SqlDmlMemory? retained) : base(metrics, budget)
        { _retained = retained; }

        private protected override void Track(object value, long bytes, bool retain, long hashChange)
        {
            if (retain && hashChange == 0 && _retained?._objects.TryGetValue(value, out var entry) == true && entry.Retained)
            { return; }
            base.Track(value, bytes, retain, hashChange);
        }
    }

    private sealed class DebugView
    {
        public DebugView(SqlDmlMemory value) { _ = value; }
    }
}

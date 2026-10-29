using System;
using System.Globalization;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using System.Threading;

namespace Assimalign.Cohesion.Database.Sql.Internal;

/// <summary>Statement grant and reference ownership for window input, sorts, computed cells and partition state.</summary>
[System.Diagnostics.DebuggerDisplay("SqlWindowMemory")]
[System.Diagnostics.DebuggerTypeProxy(typeof(DebugView))]
internal sealed class SqlWindowMemory : IDisposable
{
    private static readonly AsyncLocal<SqlWindowMemory?> _current = new();
    private readonly SqlWindowMemory? _previous;
    private readonly SqlStatementMetrics _metrics;
    private readonly long _budget;
    private long _reserved;
    private Entry[] _entries = [];
    private int[] _buckets = [];
    private int _count;
    private int _free = -1;
    private Dictionary<object?[], int>? _partitionRows;
    private long _partitionRowBytes;
    private int _partitionRowCapacity;
    private bool _admittingChildren;

    internal SqlWindowMemory(SqlStatementMetrics metrics, long budget)
    {
        _metrics = metrics;
        _budget = budget;
        _previous = _current.Value;
        Reserve(1024);
        _current.Value = this;
    }

    internal static SqlWindowMemory? Current => _current.Value;
    internal static SqlWindowMemory? For(SqlStatementMetrics? metrics)
        => Current is { } memory && ReferenceEquals(memory._metrics, metrics) ? memory : null;

    internal void Reserve(long bytes)
    {
        if (!_admittingChildren && bytes > _budget - _metrics.HashMemoryBytes)
        { throw new SqlExecutionException("CS05079", string.Format(CultureInfo.CurrentCulture, ErrorMessages.WindowMemoryBudget, _budget)); }
        _metrics.ReserveHashMemory(bytes, _budget);
        _reserved += bytes;
    }
    internal void Release(long bytes) { _metrics.ReleaseHashMemory(bytes); _reserved -= bytes; }

    // Aliased boxes, text and binary payloads own one allocation, regardless of the
    // number of keys, navigation results or aggregate caches retaining a reference.
    internal void Retain(object?[] row) => RetainResult(row, transient: _partitionRows is not null);

    // Independent children may have completed before this stage acquired its grant.
    // Admission uses the statement's existing CS05067 budget condition; subsequent
    // window work retains its window-specific CS05079 refusal.
    internal void AdoptResult(object?[] row)
    {
        _admittingChildren = true;
        try { RetainResult(row, transient: false); }
        finally { _admittingChildren = false; }
    }

    internal void RetainResult(object?[] row, bool transient)
    {
        RetainValue(row);
        foreach (var value in row) { if (value is not null) { RetainValue(value); } }
        if (transient && _partitionRows is { } rows)
        {
            if (rows.TryGetValue(row, out int references)) { rows[row] = references + 1; return; }
            if (rows.Count == _partitionRowCapacity)
            {
                int capacity = _partitionRowCapacity == 0 ? 4 : checked(_partitionRowCapacity * 2);
                // Dictionary entries/buckets use less than 64 B per requested slot,
                // including prime-capacity rounding. Charge old/new overlap first.
                long next = 64L * capacity;
                Reserve(next);
                long old = 64L * _partitionRowCapacity;
                rows.EnsureCapacity(capacity);
                _partitionRowCapacity = capacity;
                Release(old);
                _partitionRowBytes += next - old;
            }
            rows.Add(row, 1);
        }
    }

    internal void BeginPartition()
    {
        Reserve(64);
        _partitionRowBytes = 64;
        _partitionRows = new(ReferenceEqualityComparer.Instance);
    }

    internal void EndPartition()
    {
        var rows = _partitionRows!;
        _partitionRows = null;
        foreach (var entry in rows)
        {
            rows.Remove(entry.Key);
            for (int reference = 0; reference < entry.Value; reference++) { ReleaseRow(entry.Key); }
        }
        Release(_partitionRowBytes);
        _partitionRowBytes = 0;
        _partitionRowCapacity = 0;
    }

    internal void ReleaseRow(object?[] row)
    {
        // Drop the cleanup owner's reference before releasing its ledger ownership.
        // Removal is O(1), including bounded collectors repeatedly replacing rows.
        if (_partitionRows is { } rows && rows.TryGetValue(row, out int references))
        {
            if (references == 1) { rows.Remove(row); }
            else { rows[row] = references - 1; }
        }
        if (Find(row) < 0) { return; }
        foreach (var value in row) { if (value is not null) { ReleaseValue(value); } }
        ReleaseValue(row);
    }

    internal void RetainValue(object value)
    {
        int at = Find(value);
        if (at >= 0) { _entries[at].References++; return; }
        Reserve(ObjectBytes(value));
        if (_free < 0 && _count == _entries.Length) { Grow(); }
        int bucket = Bucket(value);
        if (_free >= 0) { at = _free; _free = _entries[at].Next; }
        else { at = _count++; }
        _entries[at] = new(value, _buckets[bucket], 1);
        _buckets[bucket] = at;
        if (value is SqlCharacter character) { RetainValue(character.Value); RetainValue(character.Text); }
    }

    internal void ReleaseValue(object value)
    {
        int at = Find(value);
        if (at < 0) { return; }
        if (--_entries[at].References > 0) { return; }
        int bucket = Bucket(value);
        if (_buckets[bucket] == at) { _buckets[bucket] = _entries[at].Next; }
        else
        {
            int previous = _buckets[bucket];
            while (_entries[previous].Next != at) { previous = _entries[previous].Next; }
            _entries[previous].Next = _entries[at].Next;
        }
        _entries[at] = new(null, _free, 0);
        _free = at;
        Release(ObjectBytes(value));
        if (value is SqlCharacter character) { ReleaseValue(character.Value); ReleaseValue(character.Text); }
    }

    private int Bucket(object value) => RuntimeHelpers.GetHashCode(value) & (_buckets.Length - 1);
    private int Find(object value)
    {
        if (_buckets.Length == 0) { return -1; }
        int at = _buckets[Bucket(value)];
        while (at >= 0)
        {
            if (ReferenceEquals(_entries[at].Value, value)) { return at; }
            at = _entries[at].Next;
        }
        return -1;
    }

    private void Grow()
    {
        int size = _entries.Length == 0 ? 16 : checked(_entries.Length * 2);
        // Entry is one reference and two Int32s (16 B); bucket is Int32 (4 B).
        // Both old and new arrays are charged during growth; free slots are reused.
        Reserve(48L + 20L * size);
        var entries = new Entry[size];
        var buckets = new int[size];
        Array.Fill(buckets, -1);
        Array.Copy(_entries, entries, _count);
        for (int i = 0; i < _count; i++)
        {
            if (entries[i].Value is not { } value) { continue; }
            int bucket = RuntimeHelpers.GetHashCode(value) & (size - 1);
            entries[i].Next = buckets[bucket];
            buckets[bucket] = i;
        }
        long old = _entries.Length == 0 ? 0 : 48L + 20L * _entries.Length;
        _entries = entries; _buckets = buckets;
        Release(old);
    }

    private static long ObjectBytes(object value) => value switch
    {
        // Include the reference-buffer capacity/growth allowance owned by a buffered row.
        object?[] array => 48L + 8L * array.Length,
        string text => (24L + 2L * text.Length + 7) & ~7L,
        byte[] binary => (24L + binary.Length + 7) & ~7L,
        SqlCharacter => 32L,
        decimal or DateTimeOffset or Guid => 32L,
        _ => 24L,
    };

    public void Dispose()
    {
        _current.Value = _previous;
        _entries = []; _buckets = []; _partitionRows = null;
        _metrics.ReleaseHashMemory(_reserved);
        _reserved = 0;
    }

    private record struct Entry(object? Value, int Next, int References);

    private sealed class DebugView
    {
        public DebugView(SqlWindowMemory value) { _ = value; }
    }
}

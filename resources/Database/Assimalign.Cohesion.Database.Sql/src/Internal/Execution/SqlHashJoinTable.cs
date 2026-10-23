using System;
using System.Collections.Generic;
using System.Threading;

namespace Assimalign.Cohesion.Database.Sql.Internal;

/// <summary>A statement-owned chained hash table with explicit allocation reservations and no spill.</summary>
internal sealed class SqlHashJoinTable : IDisposable
{
    private readonly SqlHashJoinPath _keys;
    private readonly SqlStatementMetrics _metrics;
    private readonly long _budget;
    private int[] _buckets = [];
    private Entry[] _entries = [];
    private int _count;
    private long _reserved;

    internal SqlHashJoinTable(SqlHashJoinPath keys, SqlStatementMetrics metrics, long budget)
    {
        _keys = keys;
        _metrics = metrics;
        _budget = budget;
        Reserve(80); // conservative table object charge, including alignment and reference fields
    }

    internal void Add(object?[] row, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!TryHash(row, _keys.InnerOrdinals, out int hash)) { return; }
        if (_count == _entries.Length) { Grow(); }
        Reserve(RowBytes(row));
        int bucket = (int)((uint)hash % (uint)_buckets.Length);
        _entries[_count] = new(hash, _buckets[bucket] - 1, row);
        _buckets[bucket] = ++_count;
    }

    internal IEnumerable<object?[]> Probe(object?[] outer, CancellationToken cancellationToken)
    {
        if (_count == 0 || !TryHash(outer, _keys.OuterOrdinals, out int hash)) { yield break; }
        int at = _buckets[(int)((uint)hash % (uint)_buckets.Length)] - 1;
        while (at >= 0)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var entry = _entries[at];
            if (entry.Hash == hash) { yield return entry.Row; }
            at = entry.Next;
        }
    }

    private bool TryHash(object?[] row, int[] ordinals, out int result)
    {
        var hash = new HashCode();
        for (int i = 0; i < ordinals.Length; i++)
        {
            if (row[ordinals[i]] is not { } value) { result = 0; return false; }
            hash.Add(SqlValueComparer.GetHashCode(value, _keys.Collations[i]));
        }
        result = hash.ToHashCode();
        return true;
    }

    private void Grow()
    {
        int capacity = _entries.Length == 0 ? 16 : checked(_entries.Length * 2);
        long bytes = 48L + 20L * capacity; // two array headers + int buckets + 16-byte entries
        Reserve(bytes); // charge both old and new arrays during growth
        var buckets = new int[capacity];
        var entries = new Entry[capacity];
        Array.Copy(_entries, entries, _count);
        for (int i = 0; i < _count; i++)
        {
            int bucket = (int)((uint)entries[i].Hash % (uint)capacity);
            entries[i] = entries[i] with { Next = buckets[bucket] - 1 };
            buckets[bucket] = i + 1;
        }
        long old = _entries.Length == 0 ? 0 : 48L + 20L * _entries.Length;
        _entries = entries;
        _buckets = buckets;
        _reserved -= old;
        _metrics.ReleaseHashMemory(old);
    }

    private static long RowBytes(object?[] row)
    {
        long bytes = 24L + 8L * row.Length;
        foreach (object? value in row)
        {
            bytes += value switch
            {
                null => 0,
                string text => (24L + 2L * text.Length + 7) & ~7L,
                byte[] binary => (24L + binary.Length + 7) & ~7L,
                // CHAR retains both its padded payload and its trimmed comparison text.
                // Charge both conservatively even when the strings happen to be identical.
                SqlCharacter text => 32L + ((24L + 2L * text.Value.Length + 7) & ~7L)
                    + ((24L + 2L * text.Text.Length + 7) & ~7L),
                _ => 32, // conservative boxed scalar charge, independent of value width
            };
        }
        return bytes;
    }

    private void Reserve(long bytes)
    {
        _metrics.ReserveHashMemory(bytes, _budget);
        _reserved += bytes;
    }

    public void Dispose()
    {
        _entries = [];
        _buckets = [];
        _metrics.ReleaseHashMemory(_reserved);
        _reserved = 0;
        _count = 0;
    }

    private readonly record struct Entry(int Hash, int Next, object?[] Row);
}

using System;
using System.Collections.Generic;
using System.Threading;

namespace Assimalign.Cohesion.Database.Sql.Internal;

/// <summary>A statement-owned chained hash table with explicit allocation reservations and no spill.</summary>
[global::System.Diagnostics.DebuggerDisplay("SqlHashJoinTable")]
[global::System.Diagnostics.DebuggerTypeProxy(typeof(global::Assimalign.Cohesion.Database.Sql.Internal.SqlHashJoinTable.DebugView))]
internal sealed class SqlHashJoinTable : IDisposable
{
    private readonly SqlHashJoinPath _keys;
    private readonly SqlStatementMetrics _metrics;
    private readonly long _budget;
    private readonly bool _trackUnmatched;
    private readonly SqlExpressionEvaluator? _evaluator;
    private bool[] _matched = [];
    private int[] _buckets = [];
    private Entry[] _entries = [];
    private int _count;
    private long _reserved;

    internal SqlHashJoinTable(SqlHashJoinPath keys, SqlStatementMetrics metrics, long budget,
        bool trackUnmatched = false, SqlExpressionEvaluator? evaluator = null)
    {
        _keys = keys;
        _metrics = metrics;
        _budget = budget;
        _trackUnmatched = trackUnmatched;
        _evaluator = evaluator;
        Reserve(104); // aligned object, including tracking/evaluator fields
    }

    internal void Add(object?[] row, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        bool hasKey = TryHash(row, _keys.InnerOrdinals, _keys.InnerExpressions, out int hash);
        if (!hasKey && !_trackUnmatched) { return; }
        if (_count == _entries.Length) { Grow(cancellationToken); }
        _metrics.DmlMemory(_budget)?.HashRow(row, adding: true);
        Reserve(RowBytes(row));
        if (!hasKey)
        {
            // -2 is outside every collision chain: NULL keys must be retained by FULL,
            // but can never be probed or rehashed into a bucket.
            _entries[_count++] = new(0, -2, row);
            return;
        }
        int bucket = (int)((uint)hash % (uint)_buckets.Length);
        _entries[_count] = new(hash, _buckets[bucket] - 1, row);
        _buckets[bucket] = ++_count;
    }

    internal IEnumerable<object?[]> Probe(object?[] outer, CancellationToken cancellationToken)
    {
        // Keep the ordinary flat-join row iterator allocation unchanged. FULL's caller
        // uses entry ordinals below so ON success can mark an individual duplicate.
        if (_count == 0 || !TryHash(outer, _keys.OuterOrdinals, _keys.OuterExpressions, out int hash)) { yield break; }
        int at = _buckets[(int)((uint)hash % (uint)_buckets.Length)] - 1;
        while (at >= 0)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var entry = _entries[at];
            if (entry.Hash == hash) { yield return entry.Row; }
            at = entry.Next;
        }
    }

    internal object?[] RowAt(int entry) => _entries[entry].Row;

    internal void MarkMatched(int entry) => _matched[entry] = true;

    internal IEnumerable<object?[]> Unmatched(CancellationToken cancellationToken)
    {
        for (int i = 0; i < _count; i++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!_matched[i]) { yield return _entries[i].Row; }
        }
    }

    internal IEnumerable<int> ProbeEntries(object?[] outer, CancellationToken cancellationToken)
    {
        if (_count == 0 || !TryHash(outer, _keys.OuterOrdinals, _keys.OuterExpressions, out int hash)) { yield break; }
        int at = _buckets[(int)((uint)hash % (uint)_buckets.Length)] - 1;
        while (at >= 0)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var entry = _entries[at];
            if (entry.Hash == hash) { yield return at; }
            at = entry.Next;
        }
    }

    private bool TryHash(object?[] row, int[] ordinals, SqlBoundExpression[]? expressions, out int result)
    {
        var hash = new HashCode();
        for (int i = 0; i < ordinals.Length; i++)
        {
            if ((expressions is null ? row[ordinals[i]] : _evaluator!.Evaluate(expressions[i], row)) is not { } value) { result = 0; return false; }
            hash.Add(SqlValueComparer.GetHashCode(value, _keys.Collations[i]));
        }
        result = hash.ToHashCode();
        return true;
    }

    private void Grow(CancellationToken cancellationToken)
    {
        int capacity = _entries.Length == 0 ? 16 : checked(_entries.Length * 2);
        long bytes = ArrayBytes(capacity);
        Reserve(bytes); // charge both old and new arrays during growth
        var buckets = new int[capacity];
        var entries = new Entry[capacity];
        var matched = _trackUnmatched ? new bool[capacity] : [];
        Array.Copy(_entries, entries, _count);
        if (_trackUnmatched) { Array.Copy(_matched, matched, _count); }
        for (int i = 0; i < _count; i++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (entries[i].Next == -2) { continue; }
            int bucket = (int)((uint)entries[i].Hash % (uint)capacity);
            entries[i] = entries[i] with { Next = buckets[bucket] - 1 };
            buckets[bucket] = i + 1;
        }
        long old = _entries.Length == 0 ? 0 : ArrayBytes(_entries.Length);
        _entries = entries;
        _buckets = buckets;
        _matched = matched;
        _reserved -= old;
        _metrics.ReleaseHashMemory(old);
    }

    private long ArrayBytes(int capacity) => 48L + 20L * capacity + (_trackUnmatched ? 24L + ((capacity + 7L) & ~7L) : 0);

    internal static long RowBytes(object?[] row)
    {
        long bytes = 24L + 8L * row.Length;
        foreach (object? value in row)
        {
            bytes += ValueBytes(value);
        }
        return bytes;
    }

    internal static long ValueBytes(object? value) => value switch
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

    private void Reserve(long bytes)
    {
        _metrics.ReserveHashMemory(bytes, _budget);
        _reserved += bytes;
    }

    public void Dispose()
    {
        _metrics.ReleaseHashMemory(_reserved);
        _reserved = 0;
        if (_count != 0 && _metrics.DmlMemory(_budget) is { } memory)
        {
            for (int i = 0; i < _count; i++) { memory.HashRow(_entries[i].Row, adding: false); }
        }
        _entries = [];
        _buckets = [];
        _matched = [];
        _count = 0;
    }

    private readonly record struct Entry(int Hash, int Next, object?[] Row);

    private sealed class DebugView
    {
        public DebugView(global::Assimalign.Cohesion.Database.Sql.Internal.SqlHashJoinTable value)
        {
            _ = value;
        }
    }
}

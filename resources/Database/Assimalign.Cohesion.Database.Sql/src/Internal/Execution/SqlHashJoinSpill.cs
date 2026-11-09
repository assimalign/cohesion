using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.Threading;

using Assimalign.Cohesion.Database.Storage;

namespace Assimalign.Cohesion.Database.Sql.Internal;

// The build is indexed once. A probe visits only its hash's chunks, backwards, and
// leaves expression evaluation and candidate accounting at the original caller boundary.
[System.Diagnostics.DebuggerDisplay("SQL indexed hash join spill")]
[System.Diagnostics.DebuggerTypeProxy(typeof(SqlHashJoinSpill.DebugView))]
internal sealed class SqlHashJoinSpill : IDisposable
{
    internal const int ChunkBytes = 8192;
    internal const long OperatorBytes = 2048;
    internal const int DirectoryEntryBytes = 48;
    internal const long ReaderBytes = 8704;
    private const int maximumOpenBuildRuns = 32;
    internal static long DirectoryBytes(int capacity) => 24 + (long)DirectoryEntryBytes * capacity;
    internal static long BitmapBytes(long count) => 24 + (((count + 7) / 8 + 7) & ~7L);
    internal static long CachedRecordBytes(object?[] row) => 32 + SqlTempRowCodec.DecodedBytes(row);
    internal static long BuildSorterGrant(int bound) => Math.Max(65536, 32768 + 2L * (8192 + bound));
    internal static long SorterCharge(long grant) => grant;
    private readonly SqlHashJoinPath _keys;
    private readonly SqlStatementMetrics _metrics;
    private readonly long _budget;
    private readonly bool _trackUnmatched;
    private readonly StorageTempStatement _statement;
    private readonly SqlHashSpillMetrics _counters;
    private readonly byte[] _families;
    private readonly Action<string, long>? _observe;
    private readonly int _bound;
    private long _sorterGrant;
    private StorageTempSorter? _sorter;
    private StorageTempRun? _prefix;
    private StorageTempRun? _index;
    private Chunk[] _directory = [];
    private int _chunks;
    private byte[] _matched = [];
    private readonly LinkedList<CachedChunk> _cache = new();
    private long _cacheBytes;
    private long _cacheGrant;
    private long _reserved;
    private long _scratch;
    private long _maximumDecoded;
    private long _maximumChunk;
    private int _count;
    private int _width = -1;
    private bool _sealed;
    private bool _disposed;
    private bool _charged;
    private bool _fullDebt;

    internal SqlHashJoinSpill(SqlDatabaseSession owner, SqlHashJoinPath keys,
        SqlStatementMetrics metrics, long budget, bool trackUnmatched)
    {
        if (keys.InnerExpressions is not null || keys.OuterExpressions is not null || budget < 262144)
        { throw SqlExecutionException.StatementMemoryBudget(budget); }
        _keys = keys; _metrics = metrics; _budget = budget; _trackUnmatched = trackUnmatched;
        _bound = (int)Math.Min(16384, (Math.Min(int.MaxValue, budget) - 32768) / 16 - 8192);
        _sorterGrant = BuildSorterGrant(_bound);
        _families = new byte[keys.InnerOrdinals.Length];
        _observe = SqlPlanExecutor.ObserveHashSpill;
        _statement = SqlHashSpillMetrics.JoinTemp(metrics, owner);
        _counters = SqlHashSpillMetrics.For(metrics)!;
        // Drain the retiring table to a single writer before allocating an arena.
        // Only this writer and bounded codec transients use the handoff allowance.
        _prefix = _statement.CreateRun();
        _observe?.Invoke("join-handoff", 0);
        if (trackUnmatched) { _counters.BeginFullJoin(); _fullDebt = true; }
    }

    internal void AdoptBuild(SqlHashJoinTable owner, CancellationToken token)
    {
        owner.ExportSpillPrefix(this, token);
        _scratch = 32768 + 2L * _bound + 2 * _maximumDecoded;
        long available = _budget - _metrics.HashMemoryBytes;
        long minimum = OperatorBytes + _families.Length + BuildSorterGrant(_bound) + _scratch;
        if (available < minimum) { Refuse(); }
        _sorterGrant = Math.Max(BuildSorterGrant(_bound), Math.Min(int.MaxValue,
            Math.Min(available / 2, available - OperatorBytes - _families.Length - _scratch)));
        Reserve(OperatorBytes + _families.Length + SorterCharge(_sorterGrant) + _scratch);
        _charged = true;
        _prefix!.Seal(token);
        _sorter = StorageTempSorter.Create(_statement, _sorterGrant, _bound, maximumOpenBuildRuns);
        foreach (var record in ReadPrefix(_prefix, token)) { _sorter.Append(record.Key.Span, record.Value.Span, token); }
        _prefix.Dispose(); _prefix = null;
    }

    internal void Add(object?[] row, CancellationToken token)
    {
        bool valid = Hash(row, _keys.InnerOrdinals, out int hash);
        if (valid || _trackUnmatched) { AddHashed(row, valid, hash, token); }
    }

    internal void AddHashed(object?[] row, bool valid, int hash, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        ValidateKeys(row, _keys.InnerOrdinals);
        if (_width < 0) { _width = row.Length; }
        if (row.Length != _width || _count == int.MaxValue) { Refuse(); }
        // Preserve the original build-record admission bound before private framing
        // removes NULL padding or packs bytes (the former metadata occupied 16 B).
        if (!SqlTempRowCodec.TryGetLength(row, out int originalLength) || originalLength + 48L > _bound) { Refuse(); }
        if (CachedRecordBytes(row) + 152 > _budget / 4) { Refuse(); }
        long decoded = SqlTempRowCodec.DecodedBytes(row) + 24 + 8L * row.Length;
        _maximumDecoded = Math.Max(_maximumDecoded, decoded);
        if (_charged)
        {
            long scratch = 32768 + 2L * _bound + 2 * _maximumDecoded;
            if (scratch > _scratch)
            {
                long extra = _sorterGrant - BuildSorterGrant(_bound);
                if (scratch - _scratch > _budget - _metrics.HashMemoryBytes && extra != 0)
                {
                    if (scratch - _scratch > _budget - _metrics.HashMemoryBytes + extra) { Refuse(); }
                    ShrinkBuildSorter(token);
                }
                Reserve(scratch - _scratch); _scratch = scratch;
            }
        }
        int start = 0, end = row.Length;
        while (start < end && row[start] is null) { start++; }
        while (end > start && row[end - 1] is null) { end--; }
        var value = Pack(Encode(row.AsSpan(start, end - start)));
        var bytes = new byte[5 + value.Length];
        bytes[0] = valid ? (byte)1 : (byte)0;
        BinaryPrimitives.WriteInt32BigEndian(bytes.AsSpan(1), start);
        value.CopyTo(bytes, 5);
        Span<byte> key = stackalloc byte[8];
        BinaryPrimitives.WriteUInt32BigEndian(key, unchecked((uint)hash));
        BinaryPrimitives.WriteUInt32BigEndian(key[4..], (uint)_count++);
        if (_charged) { _sorter!.Append(key, bytes, token); }
        else { AppendIndexRecord(_prefix!, key, bytes, token); }
        _observe?.Invoke("join-input", _count);
    }

    private static void AppendIndexRecord(StorageTempRun run, ReadOnlySpan<byte> key, ReadOnlySpan<byte> value, CancellationToken token)
    {
        var bytes = new byte[key.Length + value.Length];
        key.CopyTo(bytes); value.CopyTo(bytes.AsSpan(key.Length)); run.Append(bytes, token);
    }

    private static IEnumerable<(ReadOnlyMemory<byte> Key, ReadOnlyMemory<byte> Value)> ReadPrefix(StorageTempRun run, CancellationToken token)
    {
        using var reader = run.OpenReader(StorageTempSpace.PageSize);
        var bytes = new byte[run.MaximumRecordLength];
        while (reader.TryRead(bytes, out int size, token))
        {
            if (size < 8) { Damage(); }
            yield return (bytes.AsMemory(0, 8), bytes.AsMemory(8, size - 8));
        }
    }

    private void ShrinkBuildSorter(CancellationToken token)
    {
        // The optional arena gives way to a wider row. Consolidate under the old
        // grant, then drain that run before allocating the minimum arena.
        using var run = _statement.CreateRun(token);
        using (var sorted = _sorter!.Complete(token))
        {
            SqlHashSpillMetrics.Sort(_metrics, _statement, _sorter);
            while (sorted.MoveNext(token)) { AppendIndexRecord(run, sorted.Key.Span, sorted.Value.Span, token); }
        }
        _sorter.Dispose(); _sorter = null; run.Seal(token);
        Release(SorterCharge(_sorterGrant)); _sorterGrant = BuildSorterGrant(_bound);
        Reserve(SorterCharge(_sorterGrant));
        _sorter = StorageTempSorter.CreateWithSortedPrefix(_statement, _sorterGrant, _bound,
            ReadPrefix(run, token), maximumOpenBuildRuns, token);
        _observe?.Invoke("join-sorter-minimum", _sorterGrant);
    }

    private void Seal(CancellationToken token)
    {
        if (_sealed) { return; }
        long decodedTarget = Math.Min(_budget / 4, BuildSorterGrant(_bound) + 32768 + _bound - ReaderBytes);
        _index = _statement.CreateRun(token);
        using var directory = _statement.CreateRun(token);
        using (var sorted = _sorter!.Complete(token))
        {
            SqlHashSpillMetrics.Sort(_metrics, _statement, _sorter);
            int size = 0, count = 0, first = 0;
            uint firstHash = 0, lastHash = 0;
            long position = 0, decoded = 24, firstOrdinal = 0, lastOrdinal = -1;
            while (sorted.MoveNext(token))
            {
                if (sorted.Key.Length != 8) { Damage(); }
                var encoded = new byte[8 + sorted.Value.Length];
                sorted.Key.Span.CopyTo(encoded); sorted.Value.Span.CopyTo(encoded.AsSpan(8));
                var record = Decode(encoded);
                uint hash = unchecked((uint)record.Hash);
                if (first + count != 0 && (hash < lastHash || hash == lastHash && record.Ordinal <= lastOrdinal)) { Damage(); }
                long bytes = CachedRecordBytes(record.Row);
                if (count != 0 && (size + encoded.Length + 4 > ChunkBytes || decoded + bytes + 128 > decodedTarget))
                {
                    WriteChunk(directory, new(firstHash, lastHash, position, first, count, firstOrdinal, lastOrdinal), decoded, token);
                    first += count; count = 0; size = 0; decoded = 24;
                }
                if (count == 0) { position = _index.Position; firstHash = hash; firstOrdinal = record.Ordinal; }
                lastHash = hash; lastOrdinal = record.Ordinal; count++; size += encoded.Length + 4; decoded += bytes;
                _index.Append(encoded, token);
            }
            if (count != 0) { WriteChunk(directory, new(firstHash, lastHash, position, first, count, firstOrdinal, lastOrdinal), decoded, token); }
        }
        _sorter.Dispose(); _sorter = null;
        _index.Seal(token); directory.Seal(token);
        Release(SorterCharge(_sorterGrant) + _scratch); _scratch = 0;
        if (_trackUnmatched)
        {
            long bytes = BitmapBytes(_count);
            Reserve(bytes); _matched = new byte[(_count + 7L) / 8];
        }
        _scratch = ReaderBytes + _bound + 2 * _maximumDecoded;
        Reserve(_scratch);
        // Directory growth happens after the arena and final merge readers retire.
        // Its minimum therefore never depends on the optional build-sorter extra.
        using (var reader = directory.OpenReader(StorageTempSpace.PageSize))
        {
            Span<byte> bytes = stackalloc byte[48];
            int first = 0;
            while (reader.TryRead(bytes, out int size, token))
            {
                if (size != 48) { Damage(); }
                var chunk = new Chunk(BinaryPrimitives.ReadUInt32LittleEndian(bytes), BinaryPrimitives.ReadUInt32LittleEndian(bytes[4..]),
                    BinaryPrimitives.ReadInt64LittleEndian(bytes[8..]), BinaryPrimitives.ReadInt32LittleEndian(bytes[16..]),
                    BinaryPrimitives.ReadInt32LittleEndian(bytes[20..]), BinaryPrimitives.ReadInt64LittleEndian(bytes[24..]),
                    BinaryPrimitives.ReadInt64LittleEndian(bytes[32..]));
                long decoded = BinaryPrimitives.ReadInt64LittleEndian(bytes[40..]);
                if (chunk.First != first || chunk.Count <= 0 || chunk.Count > _count - first || decoded < 24 || decoded > _budget / 4 - 128 ||
                    chunk.FirstHash > chunk.LastHash || chunk.Position < 0 || chunk.Position >= _index.Position) { Damage(); }
                AddChunk(chunk, decoded); first += chunk.Count;
            }
            if (first != _count) { Damage(); }
        }
        _cacheGrant = Math.Min(_budget / 4, _budget - _metrics.HashMemoryBytes);
        if (_cacheGrant < _maximumChunk + 128) { Refuse(); }
        Reserve(_cacheGrant);
        _sealed = true;
        SqlHashSpillMetrics.RefreshJoin(_metrics, _statement);
        _observe?.Invoke("join-index-chunks", _chunks);
        _observe?.Invoke("join-partitioned", _count);
    }

    private static void WriteChunk(StorageTempRun run, Chunk chunk, long decoded, CancellationToken token)
    {
        Span<byte> bytes = stackalloc byte[48];
        BinaryPrimitives.WriteUInt32LittleEndian(bytes, chunk.FirstHash); BinaryPrimitives.WriteUInt32LittleEndian(bytes[4..], chunk.LastHash);
        BinaryPrimitives.WriteInt64LittleEndian(bytes[8..], chunk.Position); BinaryPrimitives.WriteInt32LittleEndian(bytes[16..], chunk.First);
        BinaryPrimitives.WriteInt32LittleEndian(bytes[20..], chunk.Count); BinaryPrimitives.WriteInt64LittleEndian(bytes[24..], chunk.FirstOrdinal);
        BinaryPrimitives.WriteInt64LittleEndian(bytes[32..], chunk.LastOrdinal); BinaryPrimitives.WriteInt64LittleEndian(bytes[40..], decoded);
        run.Append(bytes, token);
    }

    private void AddChunk(Chunk chunk, long bytes)
    {
        if (_chunks == _directory.Length)
        {
            int capacity = _chunks == 0 ? 16 : checked(_chunks * 2);
            long allocation = DirectoryBytes(capacity);
            Reserve(allocation);
            var directory = new Chunk[capacity]; _directory.CopyTo(directory, 0);
            if (_directory.Length != 0) { Release(DirectoryBytes(_directory.Length)); }
            _directory = directory;
        }
        _directory[_chunks++] = chunk; _maximumChunk = Math.Max(_maximumChunk, bytes);
    }

    internal IEnumerable<int> Probe(object?[] outer, CancellationToken token)
    {
        Seal(token);
        ValidateKeys(outer, _keys.OuterOrdinals);
        if (!Hash(outer, _keys.OuterOrdinals, out int composed)) { yield break; }
        uint hash = unchecked((uint)composed);
        int low = 0, high = _chunks;
        while (low < high)
        {
            int middle = low + (high - low) / 2;
            if (_directory[middle].FirstHash <= hash) { low = middle + 1; } else { high = middle; }
        }
        for (int chunk = low - 1; chunk >= 0 && _directory[chunk].LastHash >= hash; chunk--)
        {
            var rows = Load(chunk, token);
            for (int i = rows.Length - 1; i >= 0; i--)
            {
                token.ThrowIfCancellationRequested();
                if (unchecked((uint)rows[i].Hash) == hash && rows[i].Valid) { yield return _directory[chunk].First + i; }
            }
            rows = []; // An evicted chunk must not survive across the next allocation.
        }
    }

    private BuildRecord[] Load(int at, CancellationToken token)
    {
        for (var node = _cache.First; node is not null; node = node.Next)
        {
            if (node.Value.Index != at) { continue; }
            _cache.Remove(node); _cache.AddFirst(node); return node.Value.Rows;
        }
        // Evict before allocating; the reservation covers the maximum decoded chunk,
        // its two LRU objects and all retained cached chunks, including array headers.
        long needed = _maximumChunk + 128;
        while (_cache.Last is not null && _cacheBytes + needed > _cacheGrant) { EvictLast(); }
        var chunk = _directory[at];
        using var reader = _index!.OpenReader(StorageTempSpace.PageSize, chunk.Position);
        var buffer = new byte[_index.MaximumRecordLength];
        var rows = new BuildRecord[chunk.Count];
        uint previous = 0; long ordinal = -1, bytes = 24 + 128;
        for (int i = 0; i < rows.Length; i++)
        {
            if (!reader.TryRead(buffer, out int size, token)) { Damage(); }
            _counters.ProbeBytesRead += size + 4L;
            var record = Decode(buffer.AsSpan(0, size));
            uint hash = unchecked((uint)record.Hash);
            if (i == 0 ? hash != chunk.FirstHash || record.Ordinal != chunk.FirstOrdinal
                : hash < previous || hash == previous && record.Ordinal <= ordinal) { Damage(); }
            previous = hash; ordinal = record.Ordinal; rows[i] = record;
            bytes += CachedRecordBytes(record.Row);
        }
        if (previous != chunk.LastHash || ordinal != chunk.LastOrdinal || bytes > _cacheGrant) { Damage(); }
        _cache.AddFirst(new CachedChunk(at, rows, bytes)); _cacheBytes += bytes;
        _counters.ReadJoinChunk();
        _observe?.Invoke("join-batch-live", chunk.Count);
        return rows;
    }

    private void EvictLast()
    {
        // Keep the removed node out of Load's frame, including unoptimized builds.
        _cacheBytes -= _cache.Last!.Value.Bytes; _cache.RemoveLast();
    }

    private BuildRecord Entry(int entry)
    {
        int low = 0, high = _chunks;
        while (low < high)
        {
            int middle = low + (high - low) / 2;
            if (_directory[middle].First <= entry) { low = middle + 1; } else { high = middle; }
        }
        int chunk = low - 1;
        if (chunk < 0 || entry >= _directory[chunk].First + _directory[chunk].Count) { Damage(); }
        return Load(chunk, CancellationToken.None)[entry - _directory[chunk].First];
    }
    internal object?[] RowAt(int entry) => Entry(entry).Row;
    internal IEnumerable<object?[]> ProbeRows(object?[] outer, CancellationToken token)
    { foreach (int entry in Probe(outer, token)) { yield return RowAt(entry); } }
    internal void MarkMatched(int entry)
    { long ordinal = Entry(entry).Ordinal; _matched[ordinal / 8] |= (byte)(1 << (int)(ordinal % 8)); }

    internal IEnumerable<object?[]> Unmatched(CancellationToken token)
    {
        Seal(token);
        _cache.Clear(); _cacheBytes = 0; Release(_cacheGrant + _scratch); _cacheGrant = 0; _scratch = 0;
        if (_directory.Length != 0) { Release(DirectoryBytes(_directory.Length)); _directory = []; _chunks = 0; }
        // An ordinal sort avoids retaining a second copy of the entire build on disk.
        long readerBytes = ReaderBytes + _bound + 2 * _maximumDecoded;
        long grant = Math.Min(int.MaxValue, _budget - _metrics.HashMemoryBytes - readerBytes);
        if (grant < BuildSorterGrant(_bound)) { Refuse(); }
        Reserve(readerBytes + grant);
        try
        {
            using var sorter = StorageTempSorter.Create(_statement, grant, _bound, 64);
            _observe?.Invoke("join-match-sort-live", 0);
            using (var reader = _index!.OpenReader(StorageTempSpace.PageSize))
            {
                var buffer = new byte[_index.MaximumRecordLength];
                while (reader.TryRead(buffer, out int size, token))
                {
                    var record = Decode(buffer.AsSpan(0, size));
                    if ((_matched[record.Ordinal / 8] & 1 << (int)(record.Ordinal % 8)) != 0) { continue; }
                    var key = SqlHashSpillPartitioning.OrdinalKey(record.Ordinal, 0);
                    sorter.Append(key, Encode(record.Row), token);
                }
            }
            _index.Dispose(); _index = null;
            _matched = []; Release(BitmapBytes(_count));
            using var sorted = sorter.Complete(token);
            SqlHashSpillMetrics.Sort(_metrics, _statement, sorter);
            using var output = _statement.CreateRun(token);
            while (sorted.MoveNext(token)) { output.Append(sorted.Value.Span, token); }
            output.Seal(token); sorted.Dispose(); sorter.Dispose();
            Release(grant); grant = 0;
            // The FULL producer no longer needs a sort grant. Later collectors can use
            // the ordinary sorter; already deferred collectors finish on TakeSpilled.
            ReleaseFullDebt();
            SqlHashSpillMetrics.RefreshJoin(_metrics, _statement);
            _observe?.Invoke("join-ordered", output.RecordCount);
            using var outputReader = output.OpenReader(StorageTempSpace.PageSize);
            var bytes = new byte[output.MaximumRecordLength];
            long emitted = 0;
            while (outputReader.TryRead(bytes, out int size, token))
            {
                if (!SqlTempRowCodec.TryDecode(bytes.AsSpan(0, size), out var row) || row.Length != _width) { Damage(); yield break; }
                if ((emitted++ & 1023) == 0) { _observe?.Invoke("join-unmatched-live", emitted); }
                yield return row;
            }
        }
        finally { Release(grant + readerBytes); }
    }

    private BuildRecord Decode(ReadOnlySpan<byte> bytes)
    {
        if (bytes.Length < 14 || !SqlTempRowCodec.TryDecode(Unpack(bytes[13..]), out var cells))
        { Damage(); return default; }
        long ordinal = BinaryPrimitives.ReadUInt32BigEndian(bytes[4..]);
        int storedHash = BinaryPrimitives.ReadInt32BigEndian(bytes);
        int start = BinaryPrimitives.ReadInt32BigEndian(bytes[9..]);
        if (ordinal >= _count || bytes[8] > 1 || start < 0 || start > _width || cells.Length > _width - start) { Damage(); }
        var row = new object?[_width]; cells.CopyTo(row, start);
        for (int i = 0; i < _keys.InnerOrdinals.Length; i++)
        {
            int at = _keys.InnerOrdinals[i];
            if (at < 0 || at >= row.Length || row[at] is { } value && SqlValueComparer.ComparisonFamily(value) != _families[i]) { Damage(); }
        }
        bool valid = Hash(row, _keys.InnerOrdinals, out int hash);
        if (valid != (bytes[8] == 1) || hash != storedHash) { Damage(); }
        return new(hash, ordinal, valid, row);
    }
    private byte[] Encode(ReadOnlySpan<object?> row)
    {
        if (!SqlTempRowCodec.TryGetLength(row, out int size) || size + 32L > _bound || !SqlTempRowCodec.TryEncode(row, out var bytes))
        { Refuse(); return []; }
        return bytes;
    }

    // Private managed framing compresses repeated four-byte patterns (not SQL values).
    // This keeps booleans and padded normalized frames within the same physical quota
    // while the sorter's inputs coexist with the indexed output. Raw frames gain one
    // marker byte; B is still checked on the original exact row codec before packing.
    private static byte[] Pack(byte[] value)
    {
        var scratch = new byte[2 * value.Length + 1]; scratch[0] = 1;
        int used = 1;
        for (int at = 0; at < value.Length;)
        {
            int count = 1;
            while (count < 255 && at + 4 * (count + 1) <= value.Length &&
                value.AsSpan(at, 4).SequenceEqual(value.AsSpan(at + 4 * count, 4))) { count++; }
            if (count >= 2)
            {
                scratch[used++] = 255; scratch[used++] = (byte)count;
                value.AsSpan(at, 4).CopyTo(scratch.AsSpan(used)); used += 4; at += 4 * count;
            }
            else
            {
                byte next = value[at++]; scratch[used++] = next;
                if (next == 255) { scratch[used++] = 0; }
            }
        }
        if (used < value.Length + 1) { return scratch.AsSpan(0, used).ToArray(); }
        var raw = new byte[value.Length + 1]; value.CopyTo(raw, 1); return raw;
    }

    private ReadOnlySpan<byte> Unpack(ReadOnlySpan<byte> bytes)
    {
        if (bytes.IsEmpty || bytes[0] > 1) { Damage(); }
        if (bytes[0] == 0) { return bytes[1..]; }
        int length = 0;
        for (int at = 1; at < bytes.Length;)
        {
            if (bytes[at++] != 255) { length++; }
            else
            {
                if (at == bytes.Length) { Damage(); }
                int count = bytes[at++];
                if (count == 0) { length++; }
                else
                {
                    if (count < 2 || bytes.Length - at < 4) { Damage(); }
                    length += 4 * count; at += 4;
                }
            }
            if (length > _bound) { Damage(); }
        }
        var output = new byte[length]; int used = 0;
        for (int at = 1; at < bytes.Length;)
        {
            byte next = bytes[at++];
            if (next != 255)
            {
                if (used == output.Length) { Damage(); }
                output[used++] = next; continue;
            }
            if (at == bytes.Length) { Damage(); }
            int count = bytes[at++];
            if (count == 0)
            {
                if (used == output.Length) { Damage(); }
                output[used++] = 255; continue;
            }
            if (count < 2 || bytes.Length - at < 4 || output.Length - used < 4 * count) { Damage(); }
            for (int i = 0; i < count; i++) { bytes.Slice(at, 4).CopyTo(output.AsSpan(used)); used += 4; }
            at += 4;
        }
        return output.AsSpan(0, used);
    }
    private void ValidateKeys(object?[] row, int[] ordinals)
    {
        for (int i = 0; i < ordinals.Length; i++)
        {
            if (row[ordinals[i]] is not { } value) { continue; }
            byte family = SqlValueComparer.ComparisonFamily(value);
            if (family == 0 || _families[i] != 0 && _families[i] != family) { Refuse(); }
            _families[i] = family;
        }
    }
    private bool Hash(object?[] row, int[] ordinals, out int result)
    {
        var hash = new HashCode();
        for (int i = 0; i < ordinals.Length; i++)
        {
            if (row[ordinals[i]] is not { } value) { result = 0; return false; }
            hash.Add(SqlValueComparer.GetHashCode(value, _keys.Collations[i]));
        }
        result = hash.ToHashCode(); return true;
    }
    private void Reserve(long bytes) { _metrics.ReserveHashMemory(bytes, _budget); _reserved += bytes; }
    private void Release(long bytes) { _metrics.ReleaseHashMemory(bytes); _reserved -= bytes; }
    private void Refuse() => throw SqlExecutionException.StatementMemoryBudget(_budget);
    private void ReleaseFullDebt()
    { if (_fullDebt) { _fullDebt = false; _counters.EndFullJoin(); } }
    private static void Damage() => throw new SqlExecutionException(StorageTempException.DamageCode, ErrorMessages.SpillRowDamaged);
    public void Dispose()
    {
        if (_disposed) { return; }
        _disposed = true;
        SqlHashSpillMetrics.RefreshJoin(_metrics, _statement);
        _cache.Clear(); _directory = []; _matched = [];
        _metrics.ReleaseHashMemory(_reserved); _reserved = 0;
        ReleaseFullDebt();
        try { _sorter?.Dispose(); }
        catch (AggregateException error) { SqlHashSpillMetrics.JoinCleanup(_metrics, error); }
        try { _index?.Dispose(); }
        catch (StorageTempException error) { SqlHashSpillMetrics.JoinCleanup(_metrics, error); }
        try { _prefix?.Dispose(); }
        catch (StorageTempException error) { SqlHashSpillMetrics.JoinCleanup(_metrics, error); }
        SqlHashSpillMetrics.RefreshJoin(_metrics, _statement);
    }
    internal readonly record struct Chunk(uint FirstHash, uint LastHash, long Position, int First, int Count, long FirstOrdinal, long LastOrdinal);
    internal readonly record struct BuildRecord(int Hash, long Ordinal, bool Valid, object?[] Row);
    [System.Diagnostics.DebuggerDisplay("Cached join chunk")]
    [System.Diagnostics.DebuggerTypeProxy(typeof(CachedChunk.DebugView))]
    internal sealed class CachedChunk
    {
        internal readonly int Index;
        internal readonly BuildRecord[] Rows;
        internal readonly long Bytes;
        internal CachedChunk(int index, BuildRecord[] rows, long bytes) { Index = index; Rows = rows; Bytes = bytes; }
        private sealed class DebugView { public DebugView(CachedChunk target) { } }
    }
    private sealed class DebugView { public DebugView(SqlHashJoinSpill target) { } }
}

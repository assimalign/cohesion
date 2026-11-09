using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.Globalization;
using System.Threading;

using Assimalign.Cohesion.Database.Storage;

namespace Assimalign.Cohesion.Database.Sql.Internal;

// Demand-driven batches keep the original probe cursor in charge of evaluation. A cache
// miss spills its one evaluated probe before replay. No residual, projection or function
// executes here; callers keep their existing candidate loop and first-error boundary.
[System.Diagnostics.DebuggerDisplay("SQL hash join spill")]
[System.Diagnostics.DebuggerTypeProxy(typeof(SqlHashJoinSpill.DebugView))]
internal sealed class SqlHashJoinSpill : IDisposable
{
    private readonly SqlHashJoinPath _keys;
    private readonly SqlStatementMetrics _metrics;
    private readonly long _budget;
    private readonly bool _trackUnmatched;
    private readonly StorageTempStatement _statement;
    private readonly StorageTempPartitions _build;
    private readonly StorageTempRun?[] _flags = new StorageTempRun?[SqlHashSpillPartitioning.Count];
    private readonly byte[] _families;
    private readonly Func<int, int>? _partitionHash;
    private readonly Action<string, long>? _observe;
    private readonly int _bound;
    private long _count;
    private int _width = -1;
    private bool _sealed;
    private bool _disposed;
    private SqlHashJoinTable? _cache;
    private List<long>? _ordinals;
    private long _ordinalBytes;
    private int _cacheDepth;
    private uint _cachePrefix;
    private int _cachePartition;
    private long _scratch;
    private long _maximumDecoded;
    private bool _buildingZero;
    private bool _cacheIsOwner;

    internal SqlHashJoinSpill(SqlDatabaseSession owner, SqlHashJoinPath keys,
        SqlStatementMetrics metrics, long budget, bool trackUnmatched)
    {
        // Computed key expressions would require storing evaluated keys at the original
        // Add call, before the refusal. Keep that shape on its existing refusal path.
        if (keys.InnerExpressions is not null || keys.OuterExpressions is not null || budget < 262144)
        { throw SqlExecutionException.StatementMemoryBudget(budget); }
        _keys = keys; _metrics = metrics; _budget = budget; _trackUnmatched = trackUnmatched;
        _bound = (int)Math.Min(16384, (Math.Min(int.MaxValue, budget) - 32768) / 16 - 8192);
        _families = new byte[keys.InnerOrdinals.Length];
        _partitionHash = SqlPlanExecutor.HashSpillPartitionHash;
        _observe = SqlPlanExecutor.ObserveHashSpill;
        _statement = SqlHashSpillMetrics.JoinTemp(metrics, owner, trackUnmatched);
        _build = StorageTempPartitions.Create(_statement, SqlHashSpillPartitioning.Count, SqlHashSpillPartitioning.Count * 8704L);
        _observe?.Invoke("join-handoff", 0);
    }

    internal void Add(object?[] row, CancellationToken token)
    {
        bool valid = Hash(row, _keys.InnerOrdinals, out int hash);
        if (!valid && !_trackUnmatched) { return; }
        AddHashed(row, valid, hash, token);
    }

    internal void AddHashed(object?[] row, bool valid, int hash, CancellationToken token)
    {
        long ordinal = _count++;
        var bytes = Record(row, valid, hash, ordinal, token);
        if (_buildingZero && valid && KeepZero(hash))
        {
            while (_cache!.SpillReserved + _ordinalBytes + SqlHashJoinTable.RowBytes(row) + 112 > _budget / 4)
            {
                AdvanceZero(token);
                if (!KeepZero(hash)) { break; }
            }
            if (KeepZero(hash))
            {
                _metrics.ReserveHashMemory(32, _budget); _ordinalBytes += 32;
                _cache!.AddHashed(row, true, hash, token); _ordinals!.Add(ordinal);
                _observe?.Invoke("join-input", _count); return;
            }
        }
        _build.Append(PartitionHash(hash), bytes, token);
        _observe?.Invoke("join-input", _count);
    }

    internal bool KeepZero(int hash) => SqlHashSpillPartitioning.Prefix(PartitionHash(hash), _cacheDepth) == 0;

    internal bool InitialRow(object?[] row, bool valid, int hash, CancellationToken token)
    {
        var bytes = Record(row, valid, hash, _count++, token);
        bool keep = valid && KeepZero(hash);
        if (!keep) { _build.Append(PartitionHash(hash), bytes, token); }
        _observe?.Invoke("join-input", _count); return keep;
    }

    internal void Store(object?[] row, bool valid, int hash, long ordinal, CancellationToken token)
        => _build.Append(PartitionHash(hash), Record(row, valid, hash, ordinal, token), token);

    private byte[] Record(object?[] row, bool valid, int hash, long ordinal, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        ValidateKeys(row, _keys.InnerOrdinals);
        if (_width < 0) { _width = row.Length; }
        if (row.Length != _width) { Refuse(); }
        var record = new object?[row.Length + 3];
        record[0] = ordinal; record[1] = hash; record[2] = valid; row.CopyTo(record, 3);
        _maximumDecoded = Math.Max(_maximumDecoded, SqlTempRowCodec.DecodedBytes(record));
        return Encode(record);
    }

    internal void AdoptBatchZero(SqlHashJoinTable owner, CancellationToken token)
    {
        _cache = owner; _cacheIsOwner = true; _cacheDepth = 1; _cachePrefix = 0; _cachePartition = 0;
        owner.ExportSpillPrefix(this, token);
        while (owner.SpillReserved + 32L * owner.SpillCount > _budget / 4) { AdvanceZero(token); }
        long bytes = 32L * owner.SpillCount;
        _metrics.ReserveHashMemory(bytes, _budget); _ordinalBytes = bytes;
        _ordinals = new List<long>(owner.SpillCount);
        for (int i = 0; i < owner.SpillCount; i++) { _ordinals.Add(owner.SpillOrdinal(i)); }
        // Prefix handoff uses the fixed allowance only until the old table has drained.
        // Long-lived writer pages must be charged when several joins are building.
        const long writers = SqlHashSpillPartitioning.Count * 8704L;
        _metrics.ReserveHashMemory(writers, _budget); _scratch = writers;
        owner.RehashSpillEntries(token); _buildingZero = true;
        SqlHashSpillMetrics.Batch(_metrics, _cacheDepth);
        _observe?.Invoke("join-batch-live", owner.SpillCount);
    }

    private void AdvanceZero(CancellationToken token)
    {
        if (_cacheDepth == SqlHashSpillPartitioning.MaximumDepth)
        { SqlHashSpillMetrics.Batch(_metrics, _cacheDepth); DepthExceeded(); }
        _cacheDepth++;
        int count = _cache!.SpillCount;
        _cache.RetainSpillZero(this, _ordinals, token);
        if (_ordinals is not null)
        {
            long released = 32L * (count - _cache.SpillCount);
            _metrics.ReleaseHashMemory(released); _ordinalBytes -= released;
            _cache.RehashSpillEntries(token);
        }
        SqlHashSpillMetrics.Batch(_metrics, _cacheDepth);
    }

    internal IEnumerable<int> Probe(object?[] outer, CancellationToken token)
    {
        _maximumDecoded = Math.Max(_maximumDecoded, SqlTempRowCodec.DecodedBytes(outer));
        Seal(token);
        ValidateKeys(outer, _keys.OuterOrdinals);
        if (!Hash(outer, _keys.OuterOrdinals, out int hash)) { yield break; }
        uint partitionHash = PartitionHash(hash);
        if (_cache is null || SqlHashSpillPartitioning.Prefix(partitionHash, _cacheDepth) != _cachePrefix)
        {
            // One probe batch is sufficient: replaying a later probe before the current
            // residual would change calls and the first error. Resident probes need no file.
            using var probe = _statement.CreateRun(token);
            var record = new object?[outer.Length + 3]; record[0] = 0L; record[1] = hash; record[2] = true; outer.CopyTo(record, 3);
            probe.Append(Encode(record), token); probe.Seal(token);
            foreach (var replay in Read(probe, outer.Length, token, probe: true))
            { LoadBatch(PartitionHash((int)replay[1]!), token); }
        }
        foreach (int entry in _cache!.ProbeHashEntries(hash, token)) { yield return entry; }
    }

    internal object?[] RowAt(int entry) => _cache!.SpillRow(entry);

    internal IEnumerable<object?[]> ProbeRows(object?[] outer, CancellationToken token)
    { foreach (int entry in Probe(outer, token)) { yield return RowAt(entry); } }

    internal void MarkMatched(int entry)
    {
        if (_cache!.IsMatched(entry)) { return; }
        _cache.SpillMatch(entry);
        Span<byte> ordinal = stackalloc byte[8];
        SqlHashSpillPartitioning.WriteOrdinal(ordinal, _ordinals![entry]);
        (_flags[_cachePartition] ??= _statement.CreateRun()).Append(ordinal);
    }

    private void Seal(CancellationToken token)
    {
        if (!_sealed)
        {
            if (_buildingZero)
            {
                for (int i = 0; i < _cache!.SpillCount; i++)
                { Store(_cache.SpillRow(i), true, _cache.SpillHash(i), _ordinals![i], token); }
                _buildingZero = false;
            }
            _build.Seal(token); _sealed = true;
            SqlHashSpillMetrics.RefreshJoin(_metrics, _statement);
            _observe?.Invoke("join-partitioned", _count);
        }
        // Match-writer pages remain live across cache switches. Readers and bounded
        // codec/decoded overlap are covered as well, from the statement's same grant.
        long scratch = 32768 + (_trackUnmatched ? 65536 : 0) + 2L * _bound + 2 * _maximumDecoded;
        if (scratch > _scratch) { _metrics.ReserveHashMemory(scratch - _scratch, _budget); _scratch = scratch; }
    }

    private void LoadBatch(uint hash, CancellationToken token)
    {
        Evict();
        int partition = (int)(hash % SqlHashSpillPartitioning.Count);
        var run = _build.GetPartition(partition);
        for (int depth = 1; depth <= SqlHashSpillPartitioning.MaximumDepth; depth++)
        {
            _cache = new SqlHashJoinTable(_keys, _metrics, _budget, _trackUnmatched);
            _ordinals = new List<long>();
            long start = _metrics.HashMemoryBytes;
            bool overflow = false;
            uint prefix = SqlHashSpillPartitioning.Prefix(hash, depth);
            SqlHashSpillMetrics.Batch(_metrics, depth);
            if (run is not null)
            {
                foreach (var record in Read(run, _width, token))
                {
                    if (!(bool)record[2]! || SqlHashSpillPartitioning.Prefix(PartitionHash((int)record[1]!), depth) != prefix) { continue; }
                    var row = record[3..];
                    // Leave downstream consumers a grant too. This bounds a resident
                    // batch independently of the previous cache's favorable cardinality.
                    if (_metrics.HashMemoryBytes - start + SqlHashJoinTable.RowBytes(row) + 112 > _budget / 4)
                    { overflow = true; break; }
                    try
                    {
                        _metrics.ReserveHashMemory(32, _budget); _ordinalBytes += 32;
                        _cache.AddHashed(row, true, (int)record[1]!, token);
                        _ordinals.Add((long)record[0]!);
                    }
                    catch (SqlExecutionException error) when (error.Diagnostics[0].Code == SqlExecutionException.StatementMemoryBudgetCode)
                    { overflow = true; break; }
                }
            }
            if (!overflow)
            {
                _cacheDepth = depth; _cachePrefix = prefix; _cachePartition = partition;
                _observe?.Invoke("join-batch-live", _ordinals.Count);
                SqlHashSpillMetrics.RefreshJoin(_metrics, _statement);
                return;
            }
            Evict();
        }
        DepthExceeded();
    }

    internal IEnumerable<object?[]> Unmatched(CancellationToken token)
    {
        Seal(token); Evict();
        foreach (var flags in _flags) { flags?.Seal(token); }
        _metrics.ReleaseHashMemory(_scratch); _scratch = 0;
        // Restore the build's original entry order while merging per-batch match flags.
        // A flag sorts before the build row of the same ordinal, so duplicates need no set.
        long readerBytes = 8704 + _bound + 2 * _maximumDecoded;
        long grant = Math.Min(int.MaxValue, _budget - _metrics.HashMemoryBytes - readerBytes);
        int bound = (int)Math.Min(_bound, (grant - 32768) / 16 - 8192);
        if (grant < 65536 || bound < 32) { Refuse(); }
        _metrics.ReserveHashMemory(grant + readerBytes, _budget);
        try
        {
            using var sorter = StorageTempSorter.Create(_statement, grant, bound, 64);
            _observe?.Invoke("join-match-sort-live", 0);
            for (int partition = 0; partition < SqlHashSpillPartitioning.Count; partition++)
            {
                if (_build.GetPartition(partition) is { } run)
                {
                    foreach (var record in Read(run, _width, token))
                    {
                        var key = SqlHashSpillPartitioning.OrdinalKey((long)record[0]!, 1);
                        var value = Encode(record[3..]);
                        if (value.Length + 25L > bound) { Refuse(); }
                        sorter.Append(key, value, token);
                    }
                    run.Dispose();
                }
                if (_flags[partition] is not { } flags) { continue; }
                using var reader = flags.OpenReader(StorageTempSpace.PageSize);
                var ordinal = new byte[8];
                while (reader.TryRead(ordinal, out int size, token))
                {
                    long entry = BinaryPrimitives.ReadInt64BigEndian(ordinal);
                    if (size != 8 || entry < 0 || entry >= _count) { Damage(); }
                    var key = SqlHashSpillPartitioning.OrdinalKey(entry, 0); sorter.Append(key, [], token);
                }
                reader.Dispose(); flags.Dispose();
            }
            using var sorted = sorter.Complete(token);
            _observe?.Invoke("join-match-sort-live", _count);
            SqlHashSpillMetrics.Sort(_metrics, _statement, sorter);
            using var output = _statement.CreateRun(token);
            long matched = -1;
            while (sorted.MoveNext(token))
            {
                var key = sorted.Key.Span;
                if (key.Length != 9) { Damage(); }
                long ordinal = BinaryPrimitives.ReadInt64BigEndian(key);
                if (key[8] == 0) { matched = ordinal; continue; }
                if (key[8] != 1 || ordinal < 0 || ordinal >= _count || !SqlTempRowCodec.TryDecode(sorted.Value.Span, out var row) || row.Length != _width)
                { Damage(); yield break; }
                row = []; // Validation scratch must not survive into output-reader yields.
                if (matched != ordinal) { output.Append(sorted.Value.Span, token); }
            }
            output.Seal(token); sorted.Dispose(); sorter.Dispose();
            _metrics.ReleaseHashMemory(grant); grant = 0;
            SqlHashSpillMetrics.RefreshJoin(_metrics, _statement);
            _observe?.Invoke("join-ordered", output.RecordCount);
            using var outputReader = output.OpenReader(StorageTempSpace.PageSize);
            var buffer = new byte[output.MaximumRecordLength];
            long emitted = 0;
            while (outputReader.TryRead(buffer, out int size, token))
            {
                if (!SqlTempRowCodec.TryDecode(buffer.AsSpan(0, size), out var row) || row.Length != _width) { Damage(); yield break; }
                if ((emitted++ & 1023) == 0) { _observe?.Invoke("join-unmatched-live", emitted); }
                yield return row;
            }
        }
        finally { _metrics.ReleaseHashMemory(grant + readerBytes); }
    }

    private IEnumerable<object?[]> Read(StorageTempRun run, int width, CancellationToken token, bool probe = false)
    {
        using var reader = run.OpenReader(StorageTempSpace.PageSize);
        var buffer = new byte[run.MaximumRecordLength];
        while (reader.TryRead(buffer, out int size, token))
        {
            if (!SqlTempRowCodec.TryDecode(buffer.AsSpan(0, size), out var record) || record.Length != width + 3 ||
                record[0] is not long ordinal || ordinal < 0 || ordinal >= Math.Max(1, _count) || record[1] is not int || record[2] is not bool)
            { Damage(); yield break; }
            int[] ordinals = probe ? _keys.OuterOrdinals : _keys.InnerOrdinals;
            var row = record[3..];
            for (int i = 0; i < ordinals.Length; i++)
            {
                if (ordinals[i] < 0 || ordinals[i] >= row.Length || row[ordinals[i]] is { } value && SqlValueComparer.ComparisonFamily(value) != _families[i])
                { Damage(); }
            }
            bool valid = Hash(row, ordinals, out int hash);
            if (valid != (bool)record[2]! || hash != (int)record[1]!) { Damage(); }
            row = []; // Only the validated record crosses this iterator boundary.
            yield return record;
        }
    }

    private byte[] Encode(object?[] record)
    {
        if (!SqlTempRowCodec.TryGetLength(record, out int size) || size + 32L > _bound ||
            !SqlTempRowCodec.TryEncode(record, out var bytes)) { Refuse(); return []; }
        return bytes;
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

    private uint PartitionHash(int hash) => unchecked((uint)(_partitionHash?.Invoke(hash) ?? hash));
    private static void DepthExceeded() => throw new SqlExecutionException(SqlExecutionException.StatementMemoryBudgetCode,
        string.Format(CultureInfo.CurrentCulture, ErrorMessages.HashJoinSpillDepthExceeded, SqlHashSpillPartitioning.MaximumDepth));
    private void Refuse() => throw SqlExecutionException.StatementMemoryBudget(_budget);
    private static void Damage() => throw new SqlExecutionException(StorageTempException.DamageCode, ErrorMessages.SpillRowDamaged);

    private void Evict()
    {
        if (_cacheIsOwner) { _cache?.ClearSpillEntries(); _cacheIsOwner = false; }
        else { _cache?.Dispose(); }
        _cache = null; _ordinals = null;
        _metrics.ReleaseHashMemory(_ordinalBytes); _ordinalBytes = 0;
    }

    public void Dispose()
    {
        if (_disposed) { return; }
        _disposed = true; SqlHashSpillMetrics.RefreshJoin(_metrics, _statement); Evict();
        _metrics.ReleaseHashMemory(_scratch); _scratch = 0;
        Exception? failure = null;
        foreach (var flags in _flags)
        { try { flags?.Dispose(); } catch (StorageTempException error) { failure ??= error; } }
        try { _build.Dispose(); } catch (AggregateException error) { failure ??= error; }
        SqlHashSpillMetrics.RefreshJoin(_metrics, _statement);
        // The root reports a close failure after every iterator has released its grant;
        // a cleanup exception must never replace an earlier ON/WHERE/cancellation error.
        if (failure is not null) { SqlHashSpillMetrics.JoinCleanup(_metrics, failure); }
    }

    private sealed class DebugView { public DebugView(SqlHashJoinSpill target) { } }
}

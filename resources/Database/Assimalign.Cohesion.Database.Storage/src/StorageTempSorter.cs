using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Threading;

namespace Assimalign.Cohesion.Database.Storage;

/// <summary>A stable external balanced merge sorter over already encoded byte-comparable keys.</summary>
/// <remarks>The caller encodes direction, collation and NULL placement in each key. No SQL value
/// interpretation occurs here. Instances require serialized use; cancellation or failure disposes
/// all intermediate files. Complete transfers its returned run to the statement/caller.</remarks>
[DebuggerDisplay("StorageTempSorter {RunsCreated} runs, {MergePasses} passes")]
[DebuggerTypeProxy(typeof(DebugView))]
public sealed class StorageTempSorter : IDisposable
{
    private const int overheadBytes = 32768;
    private const int levelCount = 64;
    private readonly StorageTempStatement _statement;
    private byte[] _workspace;
    private byte[] _outputPage;
    private readonly StorageTempRun?[] _levels;
    private readonly int[] _depths;
    private readonly Comparison<int> _compareOffsets;
    private readonly int _arenaSize;
    private readonly int _maximumRecordLength;
    private int _used;
    private int _count;
    private long _ordinal;
    private bool _completed;
    private bool _disposed;

    private StorageTempSorter(StorageTempStatement statement, int grant, int maximumRecordLength, int fanIn)
    {
        _statement = statement; MemoryGrant = grant; _maximumRecordLength = maximumRecordLength; FanIn = fanIn;
        _workspace = new byte[grant - overheadBytes]; _outputPage = new byte[StorageTempSpace.PageSize];
        _levels = new StorageTempRun?[levelCount * fanIn]; _depths = new int[_levels.Length];
        _arenaSize = (int)(_workspace.Length * 4L / 5) & ~3;
        _compareOffsets = CompareOffsets;
    }

    /// <summary>Creates a sorter whose fixed byte buffers fit the supplied grant.</summary>
    /// <param name="statement">Owner of the intermediate files and their physical-byte quota.</param>
    /// <param name="memoryGrant">64 KiB to Int32.MaxValue bytes; includes workspace, page and fixed run directory.</param>
    /// <param name="maximumRecordLength">Maximum key plus value plus 16 framing bytes, at least 16.</param>
    /// <returns>A sorter with fan-in derived from its workspace and maximum record size.</returns>
    /// <exception cref="ArgumentNullException">The statement is null.</exception>
    /// <exception cref="ArgumentOutOfRangeException">The grant or record bound cannot support a two-way merge.</exception>
    /// <exception cref="ObjectDisposedException">The statement or its space is disposed.</exception>
    public static StorageTempSorter Create(StorageTempStatement statement, long memoryGrant, int maximumRecordLength)
    {
        ArgumentNullException.ThrowIfNull(statement);
        ArgumentOutOfRangeException.ThrowIfLessThan(memoryGrant, 65536);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(memoryGrant, int.MaxValue);
        ArgumentOutOfRangeException.ThrowIfLessThan(maximumRecordLength, 16);
        int workspace = (int)memoryGrant - overheadBytes;
        long fanIn = workspace / (StorageTempSpace.PageSize + (long)maximumRecordLength);
        if (fanIn < 2 || maximumRecordLength > workspace * 4L / 5)
        { throw new ArgumentOutOfRangeException(nameof(maximumRecordLength), ErrorMessages.TempSortGrantSmall); }
        lock (statement.Space.Gate)
        {
            statement.Check();
            return new StorageTempSorter(statement, (int)memoryGrant, maximumRecordLength, (int)Math.Min(16, fanIn));
        }
    }
    /// <summary>Gets the caller's memory grant.</summary>
    public long MemoryGrant { get; }
    /// <summary>Gets the merge fan-in derived from the grant and maximum record size, capped at 16.</summary>
    public int FanIn { get; }
    /// <summary>Gets the number of initial sorted runs generated.</summary>
    public long RunsCreated { get; private set; }
    /// <summary>Gets the maximum number of merge stages traversed by an output row.</summary>
    public int MergePasses { get; private set; }
    /// <summary>Gets the exact key comparisons, including run sorting and heap merging.</summary>
    public long KeyComparisons { get; private set; }
    /// <summary>Gets the records written across initial and merged runs.</summary>
    public long RecordsWritten { get; private set; }

    /// <summary>Copies a key and value; equal keys retain append order through every merge.</summary>
    /// <param name="key">An encoded byte-comparable key.</param>
    /// <param name="value">Opaque row bytes.</param>
    /// <param name="cancellationToken">Observed before run generation and during merging.</param>
    /// <exception cref="ArgumentException">The framed record exceeds the declared maximum.</exception>
    /// <exception cref="InvalidOperationException">Complete already ran.</exception>
    /// <exception cref="ObjectDisposedException">The sorter is disposed.</exception>
    /// <exception cref="System.IO.IOException">The temporary file could not be read or written.</exception>
    /// <exception cref="StorageTempException">A quota or free-space floor refused a run.</exception>
    /// <exception cref="OperationCanceledException">Cancellation was requested.</exception>
    public void Append(ReadOnlySpan<byte> key, ReadOnlySpan<byte> value, CancellationToken cancellationToken = default)
    {
        lock (_statement.Space.Gate) { AppendCore(key, value, cancellationToken); }
    }

    private void AppendCore(ReadOnlySpan<byte> key, ReadOnlySpan<byte> value, CancellationToken cancellationToken)
    {
        Check();
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            long size = 16L + key.Length + value.Length;
            if (size > _maximumRecordLength) { throw new ArgumentException(ErrorMessages.TempSortRecordLarge); }
            if (_used + size > _arenaSize || _count == (_workspace.Length - _arenaSize) / 4) { Flush(cancellationToken); }
            var row = _workspace.AsSpan(_used, (int)size);
            BinaryPrimitives.WriteInt32LittleEndian(row, key.Length);
            BinaryPrimitives.WriteInt32LittleEndian(row[4..], value.Length);
            BinaryPrimitives.WriteInt64LittleEndian(row[8..], checked(_ordinal++));
            key.CopyTo(row[16..]); value.CopyTo(row[(16 + key.Length)..]);
            MemoryMarshal.Cast<byte, int>(_workspace.AsSpan(_arenaSize))[_count++] = _used;
            _used += (int)size;
        }
        catch { Dispose(); throw; }
    }

    private void Flush(CancellationToken token)
    {
        if (_count == 0) { return; }
        var offsets = MemoryMarshal.Cast<byte, int>(_workspace.AsSpan(_arenaSize))[.._count];
        offsets.Sort(_compareOffsets);
        var run = _statement.CreateRun(token); run.UsePage(_outputPage);
        try
        {
            foreach (int offset in offsets)
            {
                token.ThrowIfCancellationRequested();
                var row = _workspace.AsSpan(offset);
                int length = 16 + BinaryPrimitives.ReadInt32LittleEndian(row) + BinaryPrimitives.ReadInt32LittleEndian(row[4..]);
                run.Append(row[..length], token); RecordsWritten++;
            }
            run.Seal(token); RunsCreated++;
        }
        catch { run.Dispose(); throw; }
        _count = 0; _used = 0;
        Push(run, 0, 0, token);
    }
    private void Push(StorageTempRun run, int level, int depth, CancellationToken token)
    {
        if (level == levelCount) { run.Dispose(); throw new InvalidOperationException(ErrorMessages.TempSortTooManyRows); }
        int start = level * FanIn;
        int slot = start;
        while (_levels[slot] is not null) { slot++; }
        _levels[slot] = run; _depths[slot] = depth;
        if (slot != start + FanIn - 1) { return; }
        var merged = Merge(start, FanIn, token);
        int nextDepth = 1;
        for (int i = start; i <= slot; i++) { nextDepth = Math.Max(nextDepth, _depths[i] + 1); _levels[i] = null; }
        MergePasses = Math.Max(MergePasses, nextDepth);
        Push(merged, level + 1, nextDepth, token);
    }

    private StorageTempRun Merge(int start, int count, CancellationToken token)
    {
        var readers = new StorageTempRunReader?[count];
        var lengths = new int[count];
        int stride = StorageTempSpace.PageSize + _maximumRecordLength;
        var heap = new PriorityQueue<int, int>(count, Comparer<int>.Create((left, right) =>
            CompareRows(_workspace.AsSpan(left * stride + StorageTempSpace.PageSize, lengths[left]),
                _workspace.AsSpan(right * stride + StorageTempSpace.PageSize, lengths[right]))));
        var output = _statement.CreateRun(token); output.UsePage(_outputPage);
        bool succeeded = false;
        try
        {
            for (int i = 0; i < count; i++)
            {
                readers[i] = _levels[start + i]!.OpenReader(_workspace.AsMemory(i * stride, StorageTempSpace.PageSize));
                if (readers[i]!.TryRead(_workspace.AsSpan(i * stride + StorageTempSpace.PageSize, _maximumRecordLength), out lengths[i], token))
                { heap.Enqueue(i, i); }
            }
            while (heap.TryDequeue(out int i, out _))
            {
                token.ThrowIfCancellationRequested();
                var row = _workspace.AsSpan(i * stride + StorageTempSpace.PageSize, lengths[i]);
                ValidateRow(row); output.Append(row, token); RecordsWritten++;
                if (readers[i]!.TryRead(_workspace.AsSpan(i * stride + StorageTempSpace.PageSize, _maximumRecordLength), out lengths[i], token))
                { heap.Enqueue(i, i); }
            }
            output.Seal(token);
            for (int i = 0; i < count; i++) { _levels[start + i]!.Dispose(); }
            succeeded = true; return output;
        }
        finally
        {
            foreach (var reader in readers) { reader?.Dispose(); }
            if (!succeeded) { output.Dispose(); }
        }
    }

    /// <summary>Finishes the sort and transfers its sealed output run to the caller/statement.</summary>
    /// <param name="cancellationToken">Observed throughout flushing and merging.</param>
    /// <returns>A sealed run read using StorageTempSortReader.Create.</returns>
    /// <exception cref="ObjectDisposedException">The sorter is disposed.</exception>
    /// <exception cref="InvalidOperationException">Complete already ran.</exception>
    /// <exception cref="System.IO.IOException">The temporary file could not be read or written.</exception>
    /// <exception cref="StorageTempException">A quota, free-space floor or integrity check failed.</exception>
    /// <exception cref="OperationCanceledException">Cancellation was requested.</exception>
    public StorageTempRun Complete(CancellationToken cancellationToken = default)
    {
        lock (_statement.Space.Gate) { return CompleteCore(cancellationToken); }
    }

    private StorageTempRun CompleteCore(CancellationToken cancellationToken)
    {
        Check();
        StorageTempRun? result = null;
        try
        {
            cancellationToken.ThrowIfCancellationRequested(); Flush(cancellationToken);
            // Collapse at most 64 levels. Keep their existing depths: each row participates in
            // at most logarithmically many balanced stages, plus this bounded final cascade.
            int resultDepth = 0;
            for (int level = 0; level < levelCount; level++)
            {
                int start = level * FanIn; int count = 0; int depth = 0;
                while (count < FanIn && _levels[start + count] is not null) { depth = Math.Max(depth, _depths[start + count]); count++; }
                if (result is not null) { _levels[start + count] = result; _depths[start + count] = resultDepth; depth = Math.Max(depth, resultDepth); count++; result = null; }
                if (count == 0) { continue; }
                result = count == 1 ? _levels[start] : Merge(start, count, cancellationToken);
                resultDepth = count == 1 ? depth : depth + 1;
                Array.Clear(_levels, start, count);
                MergePasses = Math.Max(MergePasses, resultDepth);
            }
            result ??= _statement.CreateRun(cancellationToken);
            result.Seal(cancellationToken);
            _completed = true; _workspace = []; _outputPage = [];
            return result;
        }
        catch { result?.Dispose(); Dispose(); throw; }
    }

    private int CompareOffsets(int left, int right) => CompareRows(RowAt(left), RowAt(right));
    private ReadOnlySpan<byte> RowAt(int offset)
    {
        var row = _workspace.AsSpan(offset);
        return row[..(16 + BinaryPrimitives.ReadInt32LittleEndian(row) + BinaryPrimitives.ReadInt32LittleEndian(row[4..]))];
    }
    private int CompareRows(ReadOnlySpan<byte> left, ReadOnlySpan<byte> right)
    {
        ValidateRow(left); ValidateRow(right); KeyComparisons++;
        int comparison = left.Slice(16, BinaryPrimitives.ReadInt32LittleEndian(left)).SequenceCompareTo(
            right.Slice(16, BinaryPrimitives.ReadInt32LittleEndian(right)));
        return comparison != 0 ? comparison : BinaryPrimitives.ReadInt64LittleEndian(left[8..]).CompareTo(BinaryPrimitives.ReadInt64LittleEndian(right[8..]));
    }
    internal static void ValidateRow(ReadOnlySpan<byte> row)
    {
        if (row.Length < 16 || BinaryPrimitives.ReadInt32LittleEndian(row) < 0 || BinaryPrimitives.ReadInt32LittleEndian(row[4..]) < 0 ||
            16L + BinaryPrimitives.ReadInt32LittleEndian(row) + BinaryPrimitives.ReadInt32LittleEndian(row[4..]) != row.Length)
        { throw new StorageTempException(StorageTempException.DamageCode, ErrorMessages.TempPageDamaged); }
    }
    private void Check()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        _statement.Check();
        if (_completed) { throw new InvalidOperationException(ErrorMessages.TempSortCompleted); }
    }
    /// <summary>Deletes all intermediates; a successfully returned output belongs to the caller.</summary>
    public void Dispose()
    {
        lock (_statement.Space.Gate) { DisposeCore(); }
    }

    private void DisposeCore()
    {
        if (_disposed) { return; }
        _disposed = true;
        foreach (var run in _levels) { run?.Dispose(); }
        Array.Clear(_levels); _workspace = []; _outputPage = [];
    }
    private sealed class DebugView
    {
        private readonly StorageTempSorter _target;
        public DebugView(StorageTempSorter target) { _target = target; }
        public long MemoryGrant => _target.MemoryGrant;
        public int FanIn => _target.FanIn;
        public long RunsCreated => _target.RunsCreated;
        public int MergePasses => _target.MergePasses;
        public long KeyComparisons => _target.KeyComparisons;
        public long RecordsWritten => _target.RecordsWritten;
    }
}

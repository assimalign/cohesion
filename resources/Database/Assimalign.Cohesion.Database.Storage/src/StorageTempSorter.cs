using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Threading;

using Assimalign.Cohesion.Database.Storage.Internal;

namespace Assimalign.Cohesion.Database.Storage;

/// <summary>A stable external merge sorter over already encoded byte-comparable keys.</summary>
/// <remarks>The caller encodes direction, collation and NULL placement in each key. No SQL value
/// interpretation occurs here. Instances require serialized use; cancellation or failure disposes
/// all intermediate files. Complete transfers its input runs to a streaming reader.</remarks>
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
    private int _runCount;
    private int _used;
    private int _count;
    private long _ordinal;
    private StorageTempSortReader? _finalReader;
    private long _keyComparisons;
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
        if (fanIn < 2)
        { throw new ArgumentOutOfRangeException(nameof(maximumRecordLength), ErrorMessages.TempSortGrantSmall); }
        statement.Check();
        return new StorageTempSorter(statement, (int)memoryGrant, maximumRecordLength, (int)Math.Min(16, fanIn));
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
    internal long KeyComparisons => _keyComparisons + (_finalReader?.KeyComparisons ?? 0);
    /// <summary>Gets the records written across initial and merged runs.</summary>
    internal long RecordsWritten { get; private set; }

    /// <summary>Copies a key and value; equal keys retain append order through every merge.</summary>
    /// <param name="key">An encoded byte-comparable key.</param>
    /// <param name="value">Opaque row bytes.</param>
    /// <param name="cancellationToken">Observed before run generation and during merging.</param>
    /// <exception cref="InvalidOperationException">Complete already ran.</exception>
    /// <exception cref="ObjectDisposedException">The sorter is disposed.</exception>
    /// <exception cref="StorageTempException">A quota, floor, integrity check (CK05004), oversized record (CK05006), or unavailable temporary storage (CK05005) refused a run.</exception>
    /// <exception cref="OperationCanceledException">Cancellation was requested.</exception>
    public void Append(ReadOnlySpan<byte> key, ReadOnlySpan<byte> value, CancellationToken cancellationToken = default)
    {
        AppendCore(key, value, cancellationToken);
    }

    private void AppendCore(ReadOnlySpan<byte> key, ReadOnlySpan<byte> value, CancellationToken cancellationToken)
    {
        Check();
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            long size = 16L + key.Length + value.Length;
            if (size > _maximumRecordLength)
            {
                StorageEventSource.Log.TempRefused(StorageTempException.RecordTooLargeCode);
                throw new StorageTempException(StorageTempException.RecordTooLargeCode, ErrorMessages.TempSortRecordLarge);
            }
            if (_used + size > _arenaSize || _count == (_workspace.Length - _arenaSize) / 4) { Flush(cancellationToken); }
            var row = _workspace.AsSpan(_used, (int)size);
            BinaryPrimitives.WriteInt32LittleEndian(row, key.Length);
            BinaryPrimitives.WriteInt32LittleEndian(row[4..], value.Length);
            BinaryPrimitives.WriteInt64LittleEndian(row[8..], checked(_ordinal++));
            key.CopyTo(row[16..]); value.CopyTo(row[(16 + key.Length)..]);
            MemoryMarshal.Cast<byte, int>(_workspace.AsSpan(_arenaSize))[_count++] = _used;
            _used += (int)size;
        }
        catch { try { Dispose(); } catch (AggregateException) { } throw; } // Close failures are already counted.
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
        catch { run.DisposeAfterFailure(); throw; }
        _count = 0; _used = 0;
        try { Push(run, token); }
        catch { run.DisposeAfterFailure(); throw; }
    }
    private void Push(StorageTempRun run, CancellationToken token)
    {
        _levels[_runCount] = run; _depths[_runCount++] = 0;
        // The last directory slot is reserved for a merge output beside its inputs.
        if (_runCount == _levels.Length - 1)
        { while (_runCount > _levels.Length / 2) { MergeSmallest(FanIn, token); } }
    }

    private void MergeSmallest(int count, CancellationToken token)
    {
        Array.Sort(_levels, _depths, 0, _runCount,
            Comparer<StorageTempRun?>.Create((left, right) => left!.PageCount.CompareTo(right!.PageCount)));
        var inputs = new StorageTempRun[count]; int depth = 0;
        for (int i = 0; i < count; i++) { inputs[i] = _levels[i]!; depth = Math.Max(depth, _depths[i]); }
        var output = Merge(inputs, token);
        Array.Copy(_levels, count, _levels, 0, _runCount - count);
        Array.Copy(_depths, count, _depths, 0, _runCount - count);
        Array.Clear(_levels, _runCount - count, count);
        _runCount -= count; _levels[_runCount] = output; _depths[_runCount++] = depth + 1;
        MergePasses = Math.Max(MergePasses, depth + 1);
    }

    private StorageTempRun Merge(StorageTempRun[] inputs, CancellationToken token)
    {
        using var reader = new StorageTempSortReader(_statement, inputs, _maximumRecordLength, _workspace, false);
        var output = _statement.CreateRun(token); output.UsePage(_outputPage);
        try
        {
            while (reader.MoveNext(token)) { output.Append(reader.Record, token); RecordsWritten++; }
            _keyComparisons += reader.KeyComparisons;
            output.Seal(token);
            foreach (var input in inputs) { input.Dispose(); }
            return output;
        }
        catch { output.DisposeAfterFailure(); throw; }
    }

    /// <summary>Finishes run generation and transfers the remaining inputs to a streaming merge reader.</summary>
    /// <param name="cancellationToken">Observed throughout flushing and intermediate merging.</param>
    /// <returns>A reader owning at most FanIn runs and count times (8192 + maximum record length) bytes.</returns>
    /// <exception cref="ObjectDisposedException">The sorter is disposed.</exception>
    /// <exception cref="InvalidOperationException">Complete already ran.</exception>
    /// <exception cref="StorageTempException">A quota, free-space floor, integrity check or temporary I/O failed.</exception>
    /// <exception cref="OperationCanceledException">Cancellation was requested.</exception>
    public StorageTempSortReader Complete(CancellationToken cancellationToken = default)
    {
        Check();
        try
        {
            cancellationToken.ThrowIfCancellationRequested(); Flush(cancellationToken);
            // Merge the partial group first, so later groups all use FanIn inputs.
            while (_runCount > FanIn)
            { MergeSmallest(2 + (_runCount - FanIn - 1) % (FanIn - 1), cancellationToken); }
            var runs = new StorageTempRun[_runCount];
            for (int i = 0; i < runs.Length; i++)
            {
                runs[i] = _levels[i]!;
                MergePasses = Math.Max(MergePasses, _depths[i] + (runs.Length > 1 ? 1 : 0));
            }
            _workspace = []; _outputPage = [];
            _finalReader = new StorageTempSortReader(_statement, runs, _maximumRecordLength,
                new byte[runs.Length * (StorageTempSpace.PageSize + _maximumRecordLength)], true);
            Array.Clear(_levels); _runCount = 0; _completed = true;
            return _finalReader;
        }
        catch { try { Dispose(); } catch (AggregateException) { } throw; } // Close failures are already counted.
    }

    private int CompareOffsets(int left, int right) => CompareRows(RowAt(left), RowAt(right));
    private ReadOnlySpan<byte> RowAt(int offset)
    {
        var row = _workspace.AsSpan(offset);
        return row[..(16 + BinaryPrimitives.ReadInt32LittleEndian(row) + BinaryPrimitives.ReadInt32LittleEndian(row[4..]))];
    }
    private int CompareRows(ReadOnlySpan<byte> left, ReadOnlySpan<byte> right)
    {
        ValidateRow(left); ValidateRow(right); _keyComparisons++;
        return CompareEncodedRows(left, right);
    }
    internal static int CompareEncodedRows(ReadOnlySpan<byte> left, ReadOnlySpan<byte> right)
    {
        int comparison = left.Slice(16, BinaryPrimitives.ReadInt32LittleEndian(left)).SequenceCompareTo(
            right.Slice(16, BinaryPrimitives.ReadInt32LittleEndian(right)));
        return comparison != 0 ? comparison : BinaryPrimitives.ReadInt64LittleEndian(left[8..]).CompareTo(BinaryPrimitives.ReadInt64LittleEndian(right[8..]));
    }
    internal static void ValidateRow(ReadOnlySpan<byte> row)
    {
        if (row.Length < 16 || BinaryPrimitives.ReadInt32LittleEndian(row) < 0 || BinaryPrimitives.ReadInt32LittleEndian(row[4..]) < 0 ||
            16L + BinaryPrimitives.ReadInt32LittleEndian(row) + BinaryPrimitives.ReadInt32LittleEndian(row[4..]) != row.Length)
        { throw StorageTempException.Damage(); }
    }
    private void Check()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        _statement.Check();
        if (_completed) { throw new InvalidOperationException(ErrorMessages.TempSortCompleted); }
    }
    /// <summary>Deletes intermediates; a successfully returned reader owns the remaining runs.</summary>
    /// <exception cref="AggregateException">One or more coded closes failed after all intermediates were released.</exception>
    public void Dispose()
    {
        DisposeCore();
    }

    private void DisposeCore()
    {
        if (_disposed) { return; }
        _disposed = true;
        List<Exception>? failures = null;
        foreach (var run in _levels)
        {
            try { run?.Dispose(); }
            catch (Exception error) when (error is not OutOfMemoryException)
            { (failures ??= []).Add(error); } // Continue releasing every intermediate.
        }
        Array.Clear(_levels); _workspace = []; _outputPage = [];
        if (failures is not null) { throw new AggregateException(ErrorMessages.TempCleanupFailed, failures); }
    }
    private sealed class DebugView
    {
        private readonly StorageTempSorter _target;
        public DebugView(StorageTempSorter target) { _target = target; }
        public long MemoryGrant => _target.MemoryGrant;
        public int FanIn => _target.FanIn;
        public long RunsCreated => _target.RunsCreated;
        public int MergePasses => _target.MergePasses;
    }
}

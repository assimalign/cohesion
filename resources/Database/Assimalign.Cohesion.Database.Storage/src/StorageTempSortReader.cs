using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading;

namespace Assimalign.Cohesion.Database.Storage;

/// <summary>Streams a stable final merge, owning its input runs and borrowed key/value buffers.</summary>
/// <remarks>Instances require serialized use. Key and Value remain borrowed until the next
/// MoveNext or disposal. Disposing the statement also closes every input run.</remarks>
[DebuggerDisplay("StorageTempSortReader")]
[DebuggerTypeProxy(typeof(DebugView))]
public sealed class StorageTempSortReader : IDisposable
{
    private readonly StorageTempStatement _statement;
    private readonly StorageTempRun[] _runs;
    private readonly StorageTempRunReader?[] _readers;
    private readonly int[] _lengths;
    private readonly PriorityQueue<int, int> _heap;
    private readonly int _stride;
    private readonly int _maximumRecordLength;
    private readonly bool _ownsRuns;
    private Memory<byte> _workspace;
    private int _pending = -1;
    private bool _started;
    private bool _disposed;
    internal long KeyComparisons { get; private set; }

    internal StorageTempSortReader(StorageTempStatement statement, StorageTempRun[] runs, int maximumRecordLength,
        Memory<byte> workspace, bool ownsRuns)
    {
        _statement = statement; _runs = runs; _maximumRecordLength = maximumRecordLength;
        _stride = StorageTempSpace.PageSize + maximumRecordLength;
        _workspace = workspace; _ownsRuns = ownsRuns;
        _readers = new StorageTempRunReader?[runs.Length]; _lengths = new int[runs.Length];
        _heap = new PriorityQueue<int, int>(runs.Length, Comparer<int>.Create(CompareHeads));
    }
    /// <summary>Gets the borrowed key from the current record, valid until the next read or disposal.</summary>
    public ReadOnlyMemory<byte> Key { get; private set; }
    /// <summary>Gets the borrowed opaque value, valid until the next read or disposal.</summary>
    public ReadOnlyMemory<byte> Value { get; private set; }
    internal ReadOnlySpan<byte> Record => _workspace.Span.Slice(_pending * _stride + StorageTempSpace.PageSize, _lengths[_pending]);

    /// <summary>Reads the next sorted key and value without rewriting the final merge.</summary>
    /// <param name="cancellationToken">Cancels before reading each input page or emitting a record.</param>
    /// <returns>True for a record; false at end.</returns>
    /// <exception cref="StorageTempException">A page or record is damaged, or temporary I/O failed (CK05005).</exception>
    /// <exception cref="ObjectDisposedException">The reader, statement or an input run is disposed.</exception>
    /// <exception cref="OperationCanceledException">Cancellation was requested; owned runs are released.</exception>
    public bool MoveNext(CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        Key = Value = default;
        try
        {
            _statement.Check(); cancellationToken.ThrowIfCancellationRequested();
            if (!_started)
            {
                for (int i = 0; i < _runs.Length; i++)
                {
                    _readers[i] = _runs[i].OpenReader(_workspace.Slice(i * _stride, StorageTempSpace.PageSize));
                    Refill(i, cancellationToken);
                }
                _started = true;
            }
            // Refill the previous winner only now: its borrowed key/value survived the return.
            if (_pending >= 0) { Refill(_pending, cancellationToken); _pending = -1; }
            if (!_heap.TryDequeue(out int head, out _)) { return false; }
            _pending = head;
            var row = Record;
            int keyLength = BinaryPrimitives.ReadInt32LittleEndian(row);
            int valueLength = BinaryPrimitives.ReadInt32LittleEndian(row[4..]);
            Key = _workspace.Slice(head * _stride + StorageTempSpace.PageSize + 16, keyLength);
            Value = _workspace.Slice(head * _stride + StorageTempSpace.PageSize + 16 + keyLength, valueLength);
            return true;
        }
        catch { try { Dispose(); } catch (AggregateException) { } throw; } // Preserve the initiating failure.
    }
    private void Refill(int input, CancellationToken token)
    {
        var row = _workspace.Span.Slice(input * _stride + StorageTempSpace.PageSize, _maximumRecordLength);
        if (_readers[input]!.TryRead(row, out _lengths[input], token))
        {
            StorageTempSorter.ValidateRow(row[.._lengths[input]]);
            _heap.Enqueue(input, input);
        }
        else if (_ownsRuns)
        {
            _readers[input]!.Dispose(); _readers[input] = null;
            _runs[input].DisposeAfterFailure();
        }
    }
    private int CompareHeads(int left, int right)
    {
        KeyComparisons++;
        return StorageTempSorter.CompareEncodedRows(
            _workspace.Span.Slice(left * _stride + StorageTempSpace.PageSize, _lengths[left]),
            _workspace.Span.Slice(right * _stride + StorageTempSpace.PageSize, _lengths[right]));
    }
    /// <summary>Releases the private buffers and every input run owned by this reader.</summary>
    /// <exception cref="AggregateException">One or more coded closes failed after all inputs were released.</exception>
    public void Dispose()
    {
        if (_disposed) { return; }
        _disposed = true; Key = Value = default;
        List<Exception>? failures = null;
        for (int i = 0; i < _runs.Length; i++)
        {
            _readers[i]?.Dispose(); _readers[i] = null;
            if (_ownsRuns)
            {
                try { _runs[i].Dispose(); }
                catch (Exception error) when (error is not OutOfMemoryException)
                { (failures ??= []).Add(error); } // Release every remaining input.
            }
        }
        _heap.Clear(); _workspace = default;
        if (failures is not null) { throw new AggregateException(ErrorMessages.TempCleanupFailed, failures); }
    }
    private sealed class DebugView { public DebugView(StorageTempSortReader target) { } }
}

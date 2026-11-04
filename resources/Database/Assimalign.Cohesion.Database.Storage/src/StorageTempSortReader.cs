using System;
using System.Buffers.Binary;
using System.Diagnostics;
using System.Threading;

namespace Assimalign.Cohesion.Database.Storage;

/// <summary>Reads keys and values from a completed temporary sort, borrowing memory until the next read.</summary>
[DebuggerDisplay("StorageTempSortReader")]
[DebuggerTypeProxy(typeof(DebugView))]
public sealed class StorageTempSortReader : IDisposable
{
    private readonly StorageTempRunReader _reader;
    private byte[] _record;
    private StorageTempSortReader(StorageTempRun run) { _record = new byte[run.MaximumRecordLength]; _reader = run.OpenReader(StorageTempSpace.PageSize); }
    /// <summary>Creates a reader with page and record buffers within a caller-supplied grant.</summary>
    /// <param name="run">The sealed output of StorageTempSorter.Complete.</param>
    /// <param name="memoryGrant">At least 8192 plus the run's MaximumRecordLength bytes.</param>
    /// <returns>A reader positioned before the first sorted record.</returns>
    /// <exception cref="ArgumentNullException">The run is null.</exception>
    /// <exception cref="ArgumentOutOfRangeException">The grant is too small.</exception>
    /// <exception cref="InvalidOperationException">The run is unsealed.</exception>
    /// <exception cref="ObjectDisposedException">The run is disposed.</exception>
    public static StorageTempSortReader Create(StorageTempRun run, long memoryGrant)
    {
        ArgumentNullException.ThrowIfNull(run);
        ArgumentOutOfRangeException.ThrowIfLessThan(memoryGrant, StorageTempSpace.PageSize + (long)run.MaximumRecordLength);
        return new StorageTempSortReader(run);
    }
    /// <summary>Gets the borrowed key from the current record.</summary>
    public ReadOnlyMemory<byte> Key { get; private set; }
    /// <summary>Gets the borrowed opaque value from the current record.</summary>
    public ReadOnlyMemory<byte> Value { get; private set; }
    /// <summary>Reads the next sorted key and value.</summary>
    /// <param name="cancellationToken">Cancels the read.</param>
    /// <returns>True for a record; false at end.</returns>
    /// <exception cref="System.IO.IOException">The temporary file could not be read or written.</exception>
    /// <exception cref="StorageTempException">A page or record is damaged.</exception>
    /// <exception cref="ObjectDisposedException">The reader or its run is disposed.</exception>
    /// <exception cref="OperationCanceledException">Cancellation was requested.</exception>
    public bool MoveNext(CancellationToken cancellationToken = default)
    {
        Key = Value = default;
        if (!_reader.TryRead(_record, out int length, cancellationToken)) { return false; }
        StorageTempSorter.ValidateRow(_record.AsSpan(0, length));
        int keyLength = BinaryPrimitives.ReadInt32LittleEndian(_record);
        int valueLength = BinaryPrimitives.ReadInt32LittleEndian(_record.AsSpan(4));
        Key = _record.AsMemory(16, keyLength); Value = _record.AsMemory(16 + keyLength, valueLength);
        return true;
    }
    /// <summary>Releases both private buffers. The run remains owned by its caller.</summary>
    public void Dispose() { _reader.Dispose(); _record = []; Key = Value = default; }
    private sealed class DebugView { public DebugView(StorageTempSortReader target) { } }
}

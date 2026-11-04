using System;
using System.Buffers.Binary;
using System.Diagnostics;
using System.IO;
using System.Threading;

using Assimalign.Cohesion.Database.Storage.Internal;

namespace Assimalign.Cohesion.Database.Storage;

/// <summary>An append-only byte-record run, sealed before opening independent readers.</summary>
[DebuggerDisplay("StorageTempRun {RecordCount}")]
[DebuggerTypeProxy(typeof(DebugView))]
public sealed class StorageTempRun : IDisposable
{
    internal const int HeaderSize = 32;
    internal const int PayloadSize = StorageTempSpace.PageSize - HeaderSize;
    internal const int Magic = 0x31504D54;
    private readonly StorageTempStatement _statement;
    private readonly FileStream _file;
    private Memory<byte> _page;
    private int _used;
    private long _pages;
    private long _writtenPages;
    private bool _sealed;
    private bool _disposed;
    internal StorageTempSpace Space => _statement.Space;
    internal long PageCount => _pages;

    internal StorageTempRun(StorageTempStatement statement, FileStream file) { _statement = statement; _file = file; }
    /// <summary>Gets the number of appended records.</summary>
    public long RecordCount { get; private set; }
    /// <summary>Gets the largest record length, for sizing a caller-owned read destination.</summary>
    public int MaximumRecordLength { get; private set; }
    /// <summary>Gets whether appending has finished.</summary>
    public bool IsSealed => _sealed;

    /// <summary>Copies one record, including records spanning several pages.</summary>
    /// <param name="record">The record bytes, copied before returning.</param>
    /// <param name="cancellationToken">Observed before each page.</param>
    /// <exception cref="System.IO.IOException">The temporary file could not be read or written.</exception>
    /// <exception cref="StorageTempException">A quota or free-space floor refused growth.</exception>
    /// <exception cref="InvalidOperationException">The run is sealed.</exception>
    /// <exception cref="ObjectDisposedException">The run is disposed.</exception>
    /// <exception cref="OperationCanceledException">Cancellation was requested; the run is disposed.</exception>
    /// <remarks>Any append failure disposes the run and releases every page it reserved.</remarks>
    public void Append(ReadOnlySpan<byte> record, CancellationToken cancellationToken = default)
    {
        lock (Space.Gate)
        {
            Check();
            if (_sealed) { throw new InvalidOperationException(ErrorMessages.TempRunSealed); }
            try
            {
                cancellationToken.ThrowIfCancellationRequested();
                Span<byte> length = stackalloc byte[4];
                BinaryPrimitives.WriteInt32LittleEndian(length, record.Length);
                Write(length, cancellationToken); Write(record, cancellationToken);
                RecordCount++; MaximumRecordLength = Math.Max(MaximumRecordLength, record.Length);
            }
            catch { Dispose(); throw; } // Quota, I/O, cancellation and test callback failures.
        }
    }
    internal void UsePage(Memory<byte> page) { _page = page; }
    private void Write(ReadOnlySpan<byte> bytes, CancellationToken token)
    {
        while (!bytes.IsEmpty)
        {
            token.ThrowIfCancellationRequested();
            if (_used == 0)
            {
                Space.Reserve(_statement); _pages++;
                if (_page.IsEmpty) { _page = new byte[StorageTempSpace.PageSize]; }
                _page.Span.Clear();
            }
            int take = Math.Min(bytes.Length, PayloadSize - _used);
            bytes[..take].CopyTo(_page.Span[(HeaderSize + _used)..]);
            _used += take; bytes = bytes[take..];
            if (_used == PayloadSize) { FlushPage(); }
        }
    }
    private void FlushPage()
    {
        var page = _page.Span;
        BinaryPrimitives.WriteInt32LittleEndian(page, Magic);
        BinaryPrimitives.WriteInt32LittleEndian(page[4..], 1);
        BinaryPrimitives.WriteInt64LittleEndian(page[8..], _writtenPages);
        BinaryPrimitives.WriteInt32LittleEndian(page[16..], _used);
        // The CRC covers framing as well as payload; the CRC field itself is zero while stamping.
        BinaryPrimitives.WriteUInt32LittleEndian(page[28..], 0);
        BinaryPrimitives.WriteUInt32LittleEndian(page[28..], Crc32C.Compute(page));
        // Lock order: temp-space Gate only. It covers accounting, page reservations, file I/O,
        // reads and disposal. No storage latch, buffer-pool monitor, engine lock or callback
        // that acquires them may be entered while holding Gate. The engine floor is lock-free.
        Space.BeforePageWrite?.Invoke();
        RandomAccess.Write(_file.SafeFileHandle, page, _writtenPages * StorageTempSpace.PageSize);
        _writtenPages++; Space.Written(); _used = 0;
    }

    /// <summary>Finishes the tail page and permits multiple independent readers.</summary>
    /// <param name="cancellationToken">Checked before flushing the tail.</param>
    /// <exception cref="IOException">The tail page could not be written; the run is disposed.</exception>
    /// <exception cref="OperationCanceledException">Cancellation was requested; the run is disposed.</exception>
    /// <exception cref="ObjectDisposedException">The run is disposed.</exception>
    public void Seal(CancellationToken cancellationToken = default)
    {
        lock (Space.Gate)
        {
            Check();
            try
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (_sealed) { return; }
                if (_used != 0) { FlushPage(); }
                _sealed = true; _page = Memory<byte>.Empty;
            }
            catch { Dispose(); throw; }
        }
    }

    /// <summary>Opens an independent reader with one private 8 KiB page.</summary>
    /// <param name="memoryGrant">At least one page; the destination is caller-owned.</param>
    /// <returns>A reader positioned before the first record.</returns>
    /// <exception cref="ArgumentOutOfRangeException">The grant is smaller than one page.</exception>
    /// <exception cref="InvalidOperationException">The run is not sealed.</exception>
    /// <exception cref="ObjectDisposedException">The run is disposed.</exception>
    public StorageTempRunReader OpenReader(long memoryGrant)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(memoryGrant, StorageTempSpace.PageSize);
        return OpenReader(new byte[StorageTempSpace.PageSize]);
    }
    internal StorageTempRunReader OpenReader(Memory<byte> page)
    {
        lock (Space.Gate)
        {
            Check();
            if (!_sealed) { throw new InvalidOperationException(ErrorMessages.TempRunUnsealed); }
            return new StorageTempRunReader(this, page);
        }
    }
    internal void ReadPage(long index, Span<byte> destination)
    {
        Check();
        int total = 0;
        while (total < destination.Length)
        {
            int read = RandomAccess.Read(_file.SafeFileHandle, destination[total..], index * StorageTempSpace.PageSize + total);
            if (read == 0) { throw new StorageTempException(StorageTempException.DamageCode, ErrorMessages.TempPageDamaged); }
            total += read;
        }
    }
    internal void ValidateLength()
    {
        if (RandomAccess.GetLength(_file.SafeFileHandle) != _pages * StorageTempSpace.PageSize)
        { throw new StorageTempException(StorageTempException.DamageCode, ErrorMessages.TempPageDamaged); }
    }
    internal void Check() { ObjectDisposedException.ThrowIf(_disposed, this); _statement.Check(); }
    /// <summary>Closes and deletes the file and releases every physical page, once.</summary>
    public void Dispose()
    {
        lock (Space.Gate)
        {
            if (_disposed) { return; }
            _disposed = true;
            try { _file.Dispose(); }
            finally
            {
                Space.Release(_statement, _pages * StorageTempSpace.PageSize, (_pages - _writtenPages) * StorageTempSpace.PageSize);
                _page = Memory<byte>.Empty; _statement.Forget(this);
            }
        }
    }
    private sealed class DebugView
    {
        private readonly StorageTempRun _target;
        public DebugView(StorageTempRun target) { _target = target; }
        public long RecordCount => _target.RecordCount;
        public int MaximumRecordLength => _target.MaximumRecordLength;
        public bool IsSealed => _target.IsSealed;
    }
}

using System;
using System.Buffers.Binary;
using System.Diagnostics;
using System.IO;
using System.Security.Cryptography;
using System.Threading;

using Microsoft.Win32.SafeHandles;

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
    private readonly SafeFileHandle _handle;
    internal readonly object Sync = new();
    internal readonly ulong RunId;
    internal const int FormatVersion = 2;
    private Memory<byte> _page;
    private int _used;
    private long _pages;
    private long _writtenPages;
    private bool _sealed;
    private bool _disposed;
    internal StorageTempSpace Space => _statement.Space;
    internal long PageCount => _pages;

    internal StorageTempRun(StorageTempStatement statement, FileStream file)
    {
        _statement = statement; _file = file; _handle = file.SafeFileHandle;
        Span<byte> identity = stackalloc byte[8]; RandomNumberGenerator.Fill(identity);
        RunId = BinaryPrimitives.ReadUInt64LittleEndian(identity);
    }
    /// <summary>Gets the number of appended records.</summary>
    public long RecordCount { get; private set; }
    /// <summary>Gets the largest record length, for sizing a caller-owned read destination.</summary>
    public int MaximumRecordLength { get; private set; }
    /// <summary>Gets whether appending has finished.</summary>
    public bool IsSealed => _sealed;

    /// <summary>Copies one record, including records spanning several pages.</summary>
    /// <param name="record">The record bytes, copied before returning.</param>
    /// <param name="cancellationToken">Observed before each page.</param>
    /// <exception cref="StorageTempException">A quota or free-space floor refused growth, or temporary I/O failed (CK05005).</exception>
    /// <exception cref="InvalidOperationException">The run is sealed.</exception>
    /// <exception cref="ObjectDisposedException">The run is disposed.</exception>
    /// <exception cref="OperationCanceledException">Cancellation was requested; the run is disposed.</exception>
    /// <remarks>Any append failure disposes the run and releases every page it reserved.</remarks>
    public void Append(ReadOnlySpan<byte> record, CancellationToken cancellationToken = default)
    {
        lock (Sync)
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
            catch (Exception error) when (error is IOException or UnauthorizedAccessException)
            { DisposeAfterFailure(); throw StorageTempException.Unavailable(error); }
            catch { DisposeAfterFailure(); throw; } // Quota, cancellation and test callback failures.
        }
    }
    internal void UsePage(Memory<byte> page) { _page = page; }
    internal void AppendParts(ReadOnlySpan<byte> first, ReadOnlySpan<byte> second, ReadOnlySpan<byte> third, CancellationToken token)
    {
        lock (Sync)
        {
            Check();
            if (_sealed) { throw new InvalidOperationException(ErrorMessages.TempRunSealed); }
            try
            {
                token.ThrowIfCancellationRequested();
                int size = checked(first.Length + second.Length + third.Length);
                Span<byte> length = stackalloc byte[4]; BinaryPrimitives.WriteInt32LittleEndian(length, size);
                Write(length, token); Write(first, token); Write(second, token); Write(third, token);
                RecordCount++; MaximumRecordLength = Math.Max(MaximumRecordLength, size);
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException)
            { DisposeAfterFailure(); throw StorageTempException.Unavailable(error); }
            catch { DisposeAfterFailure(); throw; }
        }
    }
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
        BinaryPrimitives.WriteInt32LittleEndian(page[4..], FormatVersion);
        BinaryPrimitives.WriteInt64LittleEndian(page[8..], _writtenPages);
        BinaryPrimitives.WriteInt32LittleEndian(page[16..], _used);
        BinaryPrimitives.WriteUInt64LittleEndian(page[20..], RunId);
        // The CRC covers framing as well as payload; the CRC field itself is zero while stamping.
        BinaryPrimitives.WriteUInt32LittleEndian(page[28..], 0);
        BinaryPrimitives.WriteUInt32LittleEndian(page[28..], Crc32C.Compute(page));
        Space.BeforePageWrite?.Invoke();
        RandomAccess.Write(_handle, page, _writtenPages * StorageTempSpace.PageSize);
        _writtenPages++; Space.Written(); _used = 0;
    }

    /// <summary>Finishes the tail page and permits multiple independent readers.</summary>
    /// <param name="cancellationToken">Checked before flushing the tail.</param>
    /// <exception cref="StorageTempException">Temporary I/O failed (CK05005); the run is disposed.</exception>
    /// <exception cref="OperationCanceledException">Cancellation was requested; the run is disposed.</exception>
    /// <exception cref="ObjectDisposedException">The run is disposed.</exception>
    public void Seal(CancellationToken cancellationToken = default)
    {
        lock (Sync)
        {
            Check();
            try
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (_sealed) { return; }
                if (_used != 0) { FlushPage(); }
                _sealed = true; _page = Memory<byte>.Empty;
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException)
            { DisposeAfterFailure(); throw StorageTempException.Unavailable(error); }
            catch { DisposeAfterFailure(); throw; }
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
        lock (Sync)
        {
            Check();
            if (!_sealed) { throw new InvalidOperationException(ErrorMessages.TempRunUnsealed); }
            return new StorageTempRunReader(this, page);
        }
    }
    internal void ReadPage(long index, Span<byte> destination)
    {
        Check();
        Space.BeforePageRead?.Invoke();
        int total = 0;
        while (total < destination.Length)
        {
            int read = RandomAccess.Read(_handle, destination[total..], index * StorageTempSpace.PageSize + total);
            if (read == 0) { throw StorageTempException.Damage(); }
            total += read;
        }
    }
    internal void ValidateLength()
    {
        if (RandomAccess.GetLength(_handle) != _pages * StorageTempSpace.PageSize)
        { throw StorageTempException.Damage(); }
    }
    internal void Check() { ObjectDisposedException.ThrowIf(_disposed, this); _statement.Check(); }
    /// <summary>Closes and deletes the file and releases every physical page, once.</summary>
    /// <exception cref="StorageTempException">Closing the temporary file failed (CK05005).</exception>
    public void Dispose()
    {
        lock (Sync)
        {
            if (_disposed) { return; }
            _disposed = true;
            bool reclaimed = false;
            try { _file.Dispose(); reclaimed = true; }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException)
            { throw StorageTempException.Unavailable(error); }
            finally
            {
                Space.Release(_statement, _pages * StorageTempSpace.PageSize, (_pages - _writtenPages) * StorageTempSpace.PageSize, reclaimed);
                _page = Memory<byte>.Empty; _statement.Forget(this);
            }
        }
    }
    internal void DisposeAfterFailure()
    {
        try { Dispose(); }
        catch (StorageTempException) { } // Closing already counted its own failure; preserve the first error.
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

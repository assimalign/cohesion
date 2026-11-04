using System;
using System.Buffers.Binary;
using System.Diagnostics;
using System.Threading;

using Assimalign.Cohesion.Database.Storage.Internal;

namespace Assimalign.Cohesion.Database.Storage;

/// <summary>Reads a sealed run with one private page and caller-owned record memory.</summary>
[DebuggerDisplay("StorageTempRunReader")]
[DebuggerTypeProxy(typeof(DebugView))]
public sealed class StorageTempRunReader : IDisposable
{
    private readonly StorageTempRun _run;
    private Memory<byte> _page;
    private long _pageIndex;
    private long _records;
    private int _position;
    private int _used;
    private bool _disposed;
    internal StorageTempRunReader(StorageTempRun run, Memory<byte> page) { _run = run; _page = page; }

    /// <summary>Copies the next record into a caller-owned destination.</summary>
    /// <param name="destination">At least the run's MaximumRecordLength bytes.</param>
    /// <param name="length">The copied length; zero at end.</param>
    /// <param name="cancellationToken">Checked before each page and record.</param>
    /// <returns>True for a record, including an empty record; false at end.</returns>
    /// <exception cref="ArgumentException">The destination cannot fit the run's largest record.</exception>
    /// <exception cref="System.IO.IOException">The temporary file could not be read or written.</exception>
    /// <exception cref="StorageTempException">A checksum, frame or record boundary is damaged.</exception>
    /// <exception cref="ObjectDisposedException">The reader, run or statement is disposed.</exception>
    /// <exception cref="OperationCanceledException">Cancellation was requested.</exception>
    public bool TryRead(Span<byte> destination, out int length, CancellationToken cancellationToken = default)
    {
        lock (_run.Space.Gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this); _run.Check(); cancellationToken.ThrowIfCancellationRequested();
            if (destination.Length < _run.MaximumRecordLength) { throw new ArgumentException(ErrorMessages.TempDestinationSmall, nameof(destination)); }
            length = 0;
            if (_records == 0 && _pageIndex == 0) { _run.ValidateLength(); }
            if (_records == _run.RecordCount)
            {
                _run.ValidateLength();
                if (_position != _used || _pageIndex != _run.PageCount) { Damage(); }
                return false;
            }
            Span<byte> header = stackalloc byte[4];
            Read(header, cancellationToken); length = BinaryPrimitives.ReadInt32LittleEndian(header);
            if (length < 0 || length > _run.MaximumRecordLength) { Damage(); }
            Read(destination[..length], cancellationToken); _records++;
            return true;
        }
    }
    private void Read(Span<byte> destination, CancellationToken token)
    {
        while (!destination.IsEmpty)
        {
            token.ThrowIfCancellationRequested();
            if (_position == _used)
            {
                if (_pageIndex >= _run.PageCount) { Damage(); }
                _run.ReadPage(_pageIndex, _page.Span);
                var page = _page.Span;
                _used = BinaryPrimitives.ReadInt32LittleEndian(page[16..]); _position = 0;
                uint crc = BinaryPrimitives.ReadUInt32LittleEndian(page[28..]);
                BinaryPrimitives.WriteUInt32LittleEndian(page[28..], 0);
                if (BinaryPrimitives.ReadInt32LittleEndian(page) != StorageTempRun.Magic || BinaryPrimitives.ReadInt32LittleEndian(page[4..]) != 1 ||
                    BinaryPrimitives.ReadInt64LittleEndian(page[8..]) != _pageIndex || _used <= 0 || _used > StorageTempRun.PayloadSize ||
                    (_pageIndex + 1 < _run.PageCount && _used != StorageTempRun.PayloadSize) || crc != Crc32C.Compute(page)) { Damage(); }
                _pageIndex++;
            }
            int take = Math.Min(destination.Length, _used - _position);
            _page.Span.Slice(StorageTempRun.HeaderSize + _position, take).CopyTo(destination);
            _position += take; destination = destination[take..];
        }
    }
    private static void Damage() => throw new StorageTempException(StorageTempException.DamageCode, ErrorMessages.TempPageDamaged);
    /// <summary>Releases the private page; the run remains available to other readers.</summary>
    public void Dispose() { lock (_run.Space.Gate) { _disposed = true; _page = Memory<byte>.Empty; } }
    private sealed class DebugView { public DebugView(StorageTempRunReader target) { } }
}

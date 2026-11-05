using System;
using System.Buffers.Binary;
using System.Diagnostics;
using System.IO;
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
    private bool _failed;
    internal StorageTempRunReader(StorageTempRun run, Memory<byte> page) { _run = run; _page = page; }

    /// <summary>Copies the next record into a caller-owned destination.</summary>
    /// <param name="destination">At least the run's MaximumRecordLength bytes.</param>
    /// <param name="length">The copied length; zero at end or on failure.</param>
    /// <param name="cancellationToken">Checked before each page and record.</param>
    /// <returns>True for a record, including an empty record; false at end.</returns>
    /// <exception cref="ArgumentException">The destination cannot fit the run's largest record.</exception>
    /// <exception cref="StorageTempException">Integrity failed, a previous read failed (CK05004), or temporary I/O failed (CK05005).</exception>
    /// <exception cref="ObjectDisposedException">The reader is disposed, or the run or statement is disposed on the first failed read; retries raise CK05004.</exception>
    /// <exception cref="OperationCanceledException">Cancellation was requested; subsequent reads fail with CK05004.</exception>
    /// <remarks>Every failure after argument validation permanently fails this reader, including
    /// cancellation before any record bytes were consumed. Argument refusals leave it usable.</remarks>
    public bool TryRead(Span<byte> destination, out int length, CancellationToken cancellationToken = default)
    {
        lock (_run.Sync)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (destination.Length < _run.MaximumRecordLength) { throw new ArgumentException(ErrorMessages.TempDestinationSmall, nameof(destination)); }
            length = 0;
            if (_failed) { throw new StorageTempException(StorageTempException.DamageCode, ErrorMessages.TempReaderFailed); }
            try
            {
                _run.Check(); cancellationToken.ThrowIfCancellationRequested();
                if (_records == 0 && _pageIndex == 0) { _run.ValidateLength(); }
                if (_records == _run.RecordCount)
                {
                    _run.ValidateLength();
                    if (_position != _used || _pageIndex != _run.PageCount) { throw StorageTempException.Damage(); }
                    return false;
                }
                Span<byte> header = stackalloc byte[4];
                Read(header, cancellationToken);
                int size = BinaryPrimitives.ReadInt32LittleEndian(header);
                if (size < 0 || size > _run.MaximumRecordLength) { throw StorageTempException.Damage(); }
                Read(destination[..size], cancellationToken); _records++;
                length = size; return true;
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException)
            { _failed = true; throw StorageTempException.Unavailable(error); }
            catch { _failed = true; throw; }
        }
    }
    private void Read(Span<byte> destination, CancellationToken token)
    {
        while (!destination.IsEmpty)
        {
            token.ThrowIfCancellationRequested();
            if (_position == _used)
            {
                if (_pageIndex >= _run.PageCount) { throw StorageTempException.Damage(); }
                _run.ReadPage(_pageIndex, _page.Span);
                token.ThrowIfCancellationRequested();
                var page = _page.Span;
                int used = BinaryPrimitives.ReadInt32LittleEndian(page[16..]);
                uint crc = BinaryPrimitives.ReadUInt32LittleEndian(page[28..]);
                BinaryPrimitives.WriteUInt32LittleEndian(page[28..], 0);
                if (BinaryPrimitives.ReadInt32LittleEndian(page) != StorageTempRun.Magic ||
                    BinaryPrimitives.ReadInt32LittleEndian(page[4..]) != StorageTempRun.FormatVersion ||
                    BinaryPrimitives.ReadInt64LittleEndian(page[8..]) != _pageIndex ||
                    BinaryPrimitives.ReadUInt64LittleEndian(page[20..]) != _run.RunId ||
                    used <= 0 || used > StorageTempRun.PayloadSize ||
                    (_pageIndex + 1 < _run.PageCount && used != StorageTempRun.PayloadSize) || crc != Crc32C.Compute(page))
                { throw StorageTempException.Damage(); }
                _used = used; _position = 0; _pageIndex++;
            }
            int take = Math.Min(destination.Length, _used - _position);
            _page.Span.Slice(StorageTempRun.HeaderSize + _position, take).CopyTo(destination);
            _position += take; destination = destination[take..];
        }
    }
    /// <summary>Releases the private page; the run remains available to other readers.</summary>
    public void Dispose() { lock (_run.Sync) { _disposed = true; _page = Memory<byte>.Empty; } }
    private sealed class DebugView { public DebugView(StorageTempRunReader target) { } }
}

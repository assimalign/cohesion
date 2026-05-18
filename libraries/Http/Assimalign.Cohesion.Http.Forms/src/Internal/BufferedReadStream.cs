using System;
using System.Buffers;
using System.Diagnostics;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

namespace Assimalign.Cohesion.Http.Internal;

/// <summary>
/// A read-only stream wrapper that buffers reads from an underlying stream
/// and exposes line-oriented + peek-ahead reads needed by the multipart and
/// form parsers.
/// </summary>
/// <remarks>
/// Ported from ASP.NET Core's <c>Microsoft.AspNetCore.WebUtilities.BufferedReadStream</c>
/// to keep the Cohesion family free of <c>Microsoft.Extensions.*</c> /
/// <c>Microsoft.AspNetCore.*</c> dependencies. Behaviour matches the upstream
/// implementation: a fixed-size byte buffer is filled on demand from the
/// inner stream, callers can <see cref="EnsureBuffered(int, CancellationToken)"/>
/// to require N bytes look-ahead, and <see cref="ReadLineAsync"/> reads up to
/// the next CRLF with an explicit per-line length cap.
/// </remarks>
internal sealed class BufferedReadStream : Stream
{
    private const byte CR = (byte)'\r';
    private const byte LF = (byte)'\n';

    private readonly Stream _inner;
    private readonly byte[] _buffer;
    private int _bufferOffset;
    private int _bufferCount;
    private bool _disposed;

    public BufferedReadStream(Stream inner, int bufferSize)
    {
        ArgumentNullException.ThrowIfNull(inner);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(bufferSize);

        _inner = inner;
        _buffer = new byte[bufferSize];
    }

    public ArraySegment<byte> BufferedData => new(_buffer, _bufferOffset, _bufferCount);

    public override bool CanRead => _inner.CanRead && !_disposed;
    public override bool CanSeek => false;
    public override bool CanWrite => false;
    public override long Length => throw new NotSupportedException();
    public override long Position
    {
        get => throw new NotSupportedException();
        set => throw new NotSupportedException();
    }

    public override void Flush() => _inner.Flush();
    public override Task FlushAsync(CancellationToken cancellationToken) => _inner.FlushAsync(cancellationToken);
    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
    public override void SetLength(long value) => throw new NotSupportedException();
    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    public override Task WriteAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
        => throw new NotSupportedException();

    public override int Read(byte[] buffer, int offset, int count)
    {
        ValidateBuffer(buffer, offset, count);
        ObjectDisposedException.ThrowIf(_disposed, this);

        if (_bufferCount > 0)
        {
            int take = Math.Min(count, _bufferCount);
            Buffer.BlockCopy(_buffer, _bufferOffset, buffer, offset, take);
            _bufferOffset += take;
            _bufferCount -= take;
            return take;
        }

        return _inner.Read(buffer, offset, count);
    }

    public override async Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
    {
        ValidateBuffer(buffer, offset, count);
        ObjectDisposedException.ThrowIf(_disposed, this);

        if (_bufferCount > 0)
        {
            int take = Math.Min(count, _bufferCount);
            Buffer.BlockCopy(_buffer, _bufferOffset, buffer, offset, take);
            _bufferOffset += take;
            _bufferCount -= take;
            return take;
        }

        return await _inner.ReadAsync(buffer.AsMemory(offset, count), cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Ensures that at least one byte is buffered. Returns false at end of
    /// stream.
    /// </summary>
    public bool EnsureBuffered()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        if (_bufferCount > 0)
        {
            return true;
        }

        _bufferOffset = 0;
        _bufferCount = _inner.Read(_buffer, 0, _buffer.Length);
        return _bufferCount > 0;
    }

    /// <summary>
    /// Ensures that at least one byte is buffered. Returns false at end of
    /// stream.
    /// </summary>
    public async Task<bool> EnsureBufferedAsync(CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        if (_bufferCount > 0)
        {
            return true;
        }

        _bufferOffset = 0;
        _bufferCount = await _inner.ReadAsync(_buffer.AsMemory(), cancellationToken).ConfigureAwait(false);
        return _bufferCount > 0;
    }

    /// <summary>
    /// Ensures that <paramref name="minCount"/> bytes are buffered (or all
    /// remaining bytes when the stream ends first). Returns false when the
    /// inner stream ends before <paramref name="minCount"/> bytes are
    /// available.
    /// </summary>
    public bool EnsureBuffered(int minCount)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentOutOfRangeException.ThrowIfNegative(minCount);

        if (minCount > _buffer.Length)
        {
            throw new ArgumentOutOfRangeException(nameof(minCount), $"Requested look-ahead {minCount} exceeds buffer size {_buffer.Length}.");
        }

        while (_bufferCount < minCount)
        {
            CompactBuffer();
            int read = _inner.Read(_buffer, _bufferOffset + _bufferCount, _buffer.Length - _bufferOffset - _bufferCount);
            if (read == 0)
            {
                return false;
            }
            _bufferCount += read;
        }

        return true;
    }

    public async Task<bool> EnsureBufferedAsync(int minCount, CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentOutOfRangeException.ThrowIfNegative(minCount);

        if (minCount > _buffer.Length)
        {
            throw new ArgumentOutOfRangeException(nameof(minCount), $"Requested look-ahead {minCount} exceeds buffer size {_buffer.Length}.");
        }

        while (_bufferCount < minCount)
        {
            CompactBuffer();
            int read = await _inner.ReadAsync(
                _buffer.AsMemory(_bufferOffset + _bufferCount, _buffer.Length - _bufferOffset - _bufferCount),
                cancellationToken).ConfigureAwait(false);
            if (read == 0)
            {
                return false;
            }
            _bufferCount += read;
        }

        return true;
    }

    /// <summary>
    /// Reads a single CRLF-terminated line into a string, capped at
    /// <paramref name="lengthLimit"/> bytes (excluding the trailing CRLF).
    /// Returns the line bytes only; the CRLF is consumed but not returned.
    /// Throws <see cref="InvalidDataException"/> when the limit is exceeded
    /// or the line is not CRLF-terminated by end of stream.
    /// </summary>
    public async Task<string> ReadLineAsync(int lengthLimit, CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        byte[] rented = ArrayPool<byte>.Shared.Rent(Math.Min(lengthLimit, 4096));
        int collected = 0;
        bool sawCr = false;

        try
        {
            while (true)
            {
                if (_bufferCount == 0 && !await EnsureBufferedAsync(cancellationToken).ConfigureAwait(false))
                {
                    throw new InvalidDataException("End of stream reached before line terminator.");
                }

                byte b = _buffer[_bufferOffset++];
                _bufferCount--;

                if (sawCr)
                {
                    if (b == LF)
                    {
                        return System.Text.Encoding.UTF8.GetString(rented, 0, collected);
                    }
                    throw new InvalidDataException("Invalid line terminator (CR without LF).");
                }

                if (b == CR)
                {
                    sawCr = true;
                    continue;
                }

                if (collected >= lengthLimit)
                {
                    throw new InvalidDataException($"Line length limit {lengthLimit} exceeded.");
                }

                if (collected >= rented.Length)
                {
                    byte[] bigger = ArrayPool<byte>.Shared.Rent(Math.Min(rented.Length * 2, lengthLimit));
                    Buffer.BlockCopy(rented, 0, bigger, 0, collected);
                    ArrayPool<byte>.Shared.Return(rented);
                    rented = bigger;
                }

                rented[collected++] = b;
            }
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(rented);
        }
    }

    /// <summary>
    /// Drains <paramref name="count"/> bytes from the front of the buffered
    /// view without copying them out. Used by the multipart reader to skip
    /// the boundary delimiter once it has been located.
    /// </summary>
    public void SkipBuffered(int count)
    {
        if (count < 0 || count > _bufferCount)
        {
            throw new ArgumentOutOfRangeException(nameof(count));
        }

        _bufferOffset += count;
        _bufferCount -= count;
    }

    protected override void Dispose(bool disposing)
    {
        if (_disposed)
        {
            return;
        }
        _disposed = true;

        if (disposing)
        {
            _inner.Dispose();
        }
    }

    private void CompactBuffer()
    {
        if (_bufferOffset == 0)
        {
            return;
        }

        Buffer.BlockCopy(_buffer, _bufferOffset, _buffer, 0, _bufferCount);
        _bufferOffset = 0;
    }

    private static void ValidateBuffer(byte[] buffer, int offset, int count)
    {
        ArgumentNullException.ThrowIfNull(buffer);
        ArgumentOutOfRangeException.ThrowIfNegative(offset);
        ArgumentOutOfRangeException.ThrowIfNegative(count);
        if (buffer.Length - offset < count)
        {
            throw new ArgumentException("Buffer too small for requested offset + count.");
        }
        Debug.Assert(buffer.Length - offset >= count);
    }
}

using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

namespace Assimalign.Cohesion.Database.Storage.Internal;

using Assimalign.Cohesion.FileSystem;

/// <summary>A sequential cursor over an owned positional handle.</summary>
internal sealed class FileHandleStream : Stream
{
    private readonly IFileSystemFileHandle _handle;
    private long _position;
    private bool _disposed;

    /// <summary>
    /// Initializes a new instance of the <see cref="FileHandleStream"/> class.
    /// </summary>
    /// <param name="handle">The positional handle the stream owns and disposes.</param>
    public FileHandleStream(IFileSystemFileHandle handle)
    {
        _handle = handle;
    }

    public override bool CanRead => !_disposed;
    public override bool CanWrite => !_disposed;
    public override bool CanSeek => !_disposed;
    public override long Length => _handle.Length;

    public override long Position
    {
        get => _position;
        set
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            ArgumentOutOfRangeException.ThrowIfNegative(value);
            _position = value;
        }
    }

    public override void Flush() => _handle.Flush(durable: false);
    public override Task FlushAsync(CancellationToken cancellationToken)
        => _handle.FlushAsync(false, cancellationToken).AsTask();

    public override int Read(byte[] buffer, int offset, int count) => Read(buffer.AsSpan(offset, count));

    public override int Read(Span<byte> buffer)
    {
        int read = _handle.Read(buffer, Position);
        _position += read;
        return read;
    }

    public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
        => ReadAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();

    public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
    {
        int read = await _handle.ReadAsync(buffer, Position, cancellationToken).ConfigureAwait(false);
        _position += read;
        return read;
    }

    public override void Write(byte[] buffer, int offset, int count) => Write(buffer.AsSpan(offset, count));

    public override void Write(ReadOnlySpan<byte> buffer)
    {
        _handle.Write(buffer, Position);
        _position += buffer.Length;
    }

    public override Task WriteAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
        => WriteAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();

    public override async ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
    {
        await _handle.WriteAsync(buffer, Position, cancellationToken).ConfigureAwait(false);
        _position += buffer.Length;
    }

    public override long Seek(long offset, SeekOrigin origin)
    {
        long position = origin switch
        {
            SeekOrigin.Begin => offset,
            SeekOrigin.Current => checked(Position + offset),
            SeekOrigin.End => checked(Length + offset),
            _ => throw new ArgumentOutOfRangeException(nameof(origin))
        };
        if (position < 0)
        {
            throw new IOException("Cannot seek before the beginning of the file.");
        }
        Position = position;
        return position;
    }

    public override void SetLength(long value) => _handle.SetLength(value);

    protected override void Dispose(bool disposing)
    {
        if (disposing && !_disposed)
        {
            _disposed = true;
            _handle.Dispose();
        }
        base.Dispose(disposing);
    }

    public override async ValueTask DisposeAsync()
    {
        if (!_disposed)
        {
            _disposed = true;
            await _handle.DisposeAsync().ConfigureAwait(false);
        }
        GC.SuppressFinalize(this);
    }
}

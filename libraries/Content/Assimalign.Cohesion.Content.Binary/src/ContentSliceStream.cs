using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

namespace Assimalign.Cohesion.Content.Binary;

/// <summary>A read-only window over a source stream, supporting random access or forward-only parsing.</summary>
/// <remarks>Not thread-safe. Random-access reads reposition the source first, allowing several slices to
/// be read in turn. A forward-only child exclusively borrows its parent's cursor until consumed or skipped.
/// Disposing a slice does not drain it. Call SkipToEnd before abandoning a forward-only child.</remarks>
public sealed class ContentSliceStream : Stream
{
    private readonly Stream _source;
    private readonly long _offset;
    private readonly long _length;
    private readonly bool _leaveOpen;
    private readonly bool _forwardOnly;
    private long _position;
    private bool _disposed;

    private ContentSliceStream(Stream source, long offset, long length, bool leaveOpen, bool forwardOnly)
    {
        _source = source;
        _offset = offset;
        _length = length;
        _leaveOpen = leaveOpen;
        _forwardOnly = forwardOnly;
        SourceOffset = source is ContentSliceStream slice ? slice.SourceOffset + offset : offset;
    }

    /// <summary>Creates a random-access window that fits entirely within a readable, seekable source.</summary>
    /// <param name="source">The source; callers must serialize access to its cursor.</param>
    /// <param name="offset">The nonnegative start relative to the source's beginning.</param>
    /// <param name="length">The nonnegative window length.</param>
    /// <param name="leaveOpen">Whether disposal leaves the source open; true by default.</param>
    /// <returns>The read-only window.</returns>
    /// <exception cref="ArgumentNullException">The source is null.</exception>
    /// <exception cref="ArgumentException">The source is not readable and seekable.</exception>
    /// <exception cref="ArgumentOutOfRangeException">The range does not fit in the source.</exception>
    /// <exception cref="System.IO.IOException">An underlying stream operation fails.</exception>
    /// <exception cref="System.ObjectDisposedException">The instance or a borrowed stream has been disposed.</exception>
    /// <exception cref="System.NotSupportedException">An underlying stream rejects a required operation.</exception>
    public static ContentSliceStream Create(Stream source, long offset, long length, bool leaveOpen = true)
    {
        ArgumentNullException.ThrowIfNull(source);
        if (!source.CanRead || !source.CanSeek)
        {
            throw new ArgumentException(ErrorMessages.SourceNotSeekable, nameof(source));
        }
        ArgumentOutOfRangeException.ThrowIfNegative(offset);
        ArgumentOutOfRangeException.ThrowIfNegative(length);
        var sourceLength = source.Length;
        ArgumentOutOfRangeException.ThrowIfGreaterThan(offset, sourceLength);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(length, sourceLength - offset);
        return new ContentSliceStream(source, offset, length, leaveOpen, forwardOnly: false);
    }

    /// <summary>Creates a forward-only window beginning at the source's current cursor.</summary>
    /// <param name="source">A readable source, including a non-seekable stream.</param>
    /// <param name="length">The nonnegative declared window length; reads may encounter source end earlier.</param>
    /// <param name="leaveOpen">Whether disposal leaves the source open; true by default.</param>
    /// <returns>The forward-only window.</returns>
    /// <exception cref="ArgumentNullException">The source is null.</exception>
    /// <exception cref="ArgumentException">The source is unreadable.</exception>
    /// <exception cref="ArgumentOutOfRangeException">The length is negative or the absolute end exceeds Int64.</exception>
    /// <exception cref="System.IO.IOException">An underlying stream operation fails.</exception>
    /// <exception cref="System.ObjectDisposedException">The instance or a borrowed stream has been disposed.</exception>
    /// <exception cref="System.NotSupportedException">An underlying stream rejects a required operation.</exception>
    public static ContentSliceStream CreateForwardOnly(Stream source, long length, bool leaveOpen = true)
    {
        ArgumentNullException.ThrowIfNull(source);
        if (!source.CanRead)
        {
            throw new ArgumentException(ErrorMessages.SourceNotReadable, nameof(source));
        }
        ArgumentOutOfRangeException.ThrowIfNegative(length);
        var offset = source.CanSeek || source is ContentSliceStream ? source.Position : 0;
        var origin = source is ContentSliceStream slice ? slice.SourceOffset : 0;
        ArgumentOutOfRangeException.ThrowIfGreaterThan(offset, long.MaxValue - origin);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(length, long.MaxValue - origin - offset);
        return new ContentSliceStream(source, offset, length, leaveOpen, forwardOnly: true);
    }

    /// <summary>Gets the absolute start in the source coordinate system, accumulating enclosing slice offsets.</summary>
    /// <remarks>For a forward-only slice, this uses the source's creation-time Position if it can seek or is
    /// itself a ContentSliceStream; otherwise it starts at zero. An unknown source origin remains unknown.</remarks>
    public long SourceOffset { get; }

    /// <summary>Gets the unread bytes in the window; zero when positioned at or past its end.</summary>
    /// <exception cref="System.ObjectDisposedException">The instance has been disposed.</exception>
    public long Remaining
    {
        get
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            return Math.Max(0, _length - _position);
        }
    }

    /// <inheritdoc/>
    public override bool CanRead => !_disposed && _source.CanRead;

    /// <inheritdoc/>
    public override bool CanSeek => !_disposed && !_forwardOnly && _source.CanSeek;

    /// <inheritdoc/>
    public override bool CanWrite => false;

    /// <summary>Gets the declared window length, including in forward-only mode.</summary>
    /// <exception cref="ObjectDisposedException">The slice is disposed.</exception>
    public override long Length
    {
        get
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            return _length;
        }
    }

    /// <summary>Gets the window-relative cursor (bytes consumed in forward-only mode), or sets a seekable cursor.</summary>
    /// <remarks>Like FileStream, a seekable cursor may pass the end; subsequent reads return zero.</remarks>
    /// <exception cref="ObjectDisposedException">The slice is disposed.</exception>
    /// <exception cref="NotSupportedException">A forward-only cursor is assigned.</exception>
    /// <exception cref="ArgumentOutOfRangeException">The assigned position is negative.</exception>
    public override long Position
    {
        get
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            return _position;
        }
        set
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (!CanSeek)
            {
                throw new NotSupportedException(ErrorMessages.SliceNotSeekable);
            }
            ArgumentOutOfRangeException.ThrowIfNegative(value);
            _position = value;
        }
    }

    /// <summary>Creates a borrowed nested window relative to this window's beginning.</summary>
    /// <param name="offset">The nonnegative child start relative to this window.</param>
    /// <param name="length">The nonnegative child length.</param>
    /// <returns>A child that leaves this stream open.</returns>
    /// <remarks>Forward-only parents cannot rewind: creation skips from the current cursor to the child start.
    /// Format readers must validate declared child sizes and classify invalid structures before calling this method.</remarks>
    /// <exception cref="ObjectDisposedException">This slice is disposed.</exception>
    /// <exception cref="ArgumentOutOfRangeException">The child does not fit, or requires rewinding a forward-only parent.</exception>
    /// <exception cref="ContentFormatException">Skipping to a forward-only child encounters truncated input.</exception>
    /// <exception cref="System.IO.IOException">An underlying stream operation fails.</exception>
    /// <exception cref="System.NotSupportedException">An underlying stream rejects a required operation.</exception>
    public ContentSliceStream CreateSlice(long offset, long length)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentOutOfRangeException.ThrowIfNegative(offset);
        ArgumentOutOfRangeException.ThrowIfNegative(length);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(offset, _length);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(length, _length - offset);
        if (CanSeek)
        {
            return Create(this, offset, length);
        }
        ArgumentOutOfRangeException.ThrowIfLessThan(offset, _position);
        this.SkipExactly(offset - _position);
        return CreateForwardOnly(this, length);
    }

    /// <summary>Advances to the window end; forward-only sources are drained through a pooled buffer.</summary>
    /// <exception cref="ObjectDisposedException">The slice is disposed.</exception>
    /// <exception cref="ContentFormatException">The source ends before the window end.</exception>
    /// <exception cref="System.IO.IOException">An underlying stream operation fails.</exception>
    /// <exception cref="System.NotSupportedException">An underlying stream rejects a required operation.</exception>
    public void SkipToEnd() => this.SkipExactly(Remaining);

    /// <summary>Advances to the window end; forward-only sources are drained through a pooled buffer.</summary>
    /// <param name="cancellationToken">A token used to cancel the operation.</param>
    /// <returns>A task completing when the window end is reached.</returns>
    /// <exception cref="ObjectDisposedException">The slice is disposed.</exception>
    /// <exception cref="ContentFormatException">The source ends before the window end.</exception>
    /// <exception cref="OperationCanceledException">The nonempty operation is canceled.</exception>
    /// <exception cref="System.IO.IOException">An underlying stream operation fails.</exception>
    /// <exception cref="System.NotSupportedException">An underlying stream rejects a required operation.</exception>
    public ValueTask SkipToEndAsync(CancellationToken cancellationToken = default) =>
        this.SkipExactlyAsync(Remaining, cancellationToken);

    /// <inheritdoc/>
    public override int Read(byte[] buffer, int offset, int count)
    {
        ArgumentNullException.ThrowIfNull(buffer);
        return Read(buffer.AsSpan(offset, count));
    }

    /// <summary>Reads up to the remaining bytes, without crossing the window or treating a short read as an error.</summary>
    /// <param name="buffer">The destination.</param>
    /// <returns>The count read, or zero at the source or window end.</returns>
    /// <exception cref="ObjectDisposedException">The slice is disposed.</exception>
    /// <exception cref="System.IO.IOException">An underlying stream operation fails.</exception>
    /// <exception cref="System.NotSupportedException">An underlying stream rejects a required operation.</exception>
    public override int Read(Span<byte> buffer)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        var count = (int)Math.Min(buffer.Length, Remaining);
        if (count == 0)
        {
            return 0;
        }
        if (!_forwardOnly)
        {
            _source.Position = _offset + _position;
        }
        var read = _source.Read(buffer[..count]);
        _position += read;
        return read;
    }

    /// <inheritdoc/>
    public override int ReadByte()
    {
        Span<byte> buffer = stackalloc byte[1];
        return Read(buffer) == 0 ? -1 : buffer[0];
    }

    /// <inheritdoc/>
    /// <exception cref="System.IO.IOException">An underlying stream operation fails.</exception>
    public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(buffer);
        return ReadAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();
    }

    /// <summary>Reads up to the remaining bytes without allocating an intermediate buffer.</summary>
    /// <param name="buffer">The destination.</param>
    /// <param name="cancellationToken">A token used to cancel the operation.</param>
    /// <returns>The count read, or zero at the source or window end.</returns>
    /// <exception cref="ObjectDisposedException">The slice is disposed.</exception>
    /// <exception cref="OperationCanceledException">The nonempty operation is canceled.</exception>
    /// <exception cref="System.IO.IOException">An underlying stream operation fails.</exception>
    /// <exception cref="System.NotSupportedException">An underlying stream rejects a required operation.</exception>
    public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        var count = (int)Math.Min(buffer.Length, Remaining);
        if (count == 0)
        {
            return 0;
        }
        cancellationToken.ThrowIfCancellationRequested();
        if (!_forwardOnly)
        {
            _source.Position = _offset + _position;
        }
        var read = await _source.ReadAsync(buffer[..count], cancellationToken).ConfigureAwait(false);
        _position += read;
        return read;
    }

    /// <summary>Moves the seekable cursor relative to the window, permitting positions beyond its end like FileStream.</summary>
    /// <param name="offset">The displacement from the chosen origin.</param>
    /// <param name="origin">Beginning, current cursor, or window end.</param>
    /// <returns>The new window-relative position.</returns>
    /// <exception cref="ObjectDisposedException">The slice is disposed.</exception>
    /// <exception cref="NotSupportedException">The slice is forward-only.</exception>
    /// <exception cref="ArgumentException">The origin is invalid.</exception>
    /// <exception cref="IOException">The target is negative or cannot be represented by Int64.</exception>
    public override long Seek(long offset, SeekOrigin origin)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (!CanSeek)
        {
            throw new NotSupportedException(ErrorMessages.SliceNotSeekable);
        }
        var start = origin switch
        {
            SeekOrigin.Begin => 0,
            SeekOrigin.Current => _position,
            SeekOrigin.End => _length,
            _ => throw new ArgumentException(ErrorMessages.InvalidSeekOrigin, nameof(origin))
        };
        if (offset < -start || offset > long.MaxValue - start)
        {
            throw new IOException(ErrorMessages.SeekOutsideRange);
        }
        _position = start + offset;
        return _position;
    }

    /// <inheritdoc/>
    /// <exception cref="System.ObjectDisposedException">The instance has been disposed.</exception>
    public override void Flush() => ObjectDisposedException.ThrowIf(_disposed, this);

    /// <inheritdoc/>
    public override Task FlushAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        Flush();
        return Task.CompletedTask;
    }

    /// <inheritdoc/>
    public override void SetLength(long value) => throw new NotSupportedException(ErrorMessages.SliceReadOnly);

    /// <inheritdoc/>
    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException(ErrorMessages.SliceReadOnly);

    /// <inheritdoc/>
    /// <exception cref="System.NotSupportedException">The slice is read-only.</exception>
    public override void Write(ReadOnlySpan<byte> buffer) => throw new NotSupportedException(ErrorMessages.SliceReadOnly);

    /// <inheritdoc/>
    public override void WriteByte(byte value) => throw new NotSupportedException(ErrorMessages.SliceReadOnly);

    /// <inheritdoc/>
    public override Task WriteAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken = default) =>
        throw new NotSupportedException(ErrorMessages.SliceReadOnly);

    /// <inheritdoc/>
    /// <exception cref="System.NotSupportedException">The slice is read-only.</exception>
    public override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default) =>
        throw new NotSupportedException(ErrorMessages.SliceReadOnly);

    /// <inheritdoc/>
    protected override void Dispose(bool disposing)
    {
        if (!_disposed)
        {
            _disposed = true;
            if (disposing && !_leaveOpen)
            {
                _source.Dispose();
            }
        }
        base.Dispose(disposing);
    }

    /// <inheritdoc/>
    public override async ValueTask DisposeAsync()
    {
        if (!_disposed)
        {
            _disposed = true;
            if (!_leaveOpen)
            {
                await _source.DisposeAsync().ConfigureAwait(false);
            }
        }
        base.Dispose(false);
        GC.SuppressFinalize(this);
    }
}

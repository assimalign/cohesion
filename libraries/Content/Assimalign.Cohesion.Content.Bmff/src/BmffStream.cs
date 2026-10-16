using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

using Assimalign.Cohesion.Content.Binary;
using Assimalign.Cohesion.Content.Bmff.Internal;

namespace Assimalign.Cohesion.Content.Bmff;

/// <summary>A borrowed, bounded box payload stream with a window-relative cursor.</summary>
/// <remarks>Offset is absolute; Position and Length are window-relative. Disposal leaves the parent open.
/// Forward-only windows exclusively borrow the parent until consumed. Reads may return short counts.</remarks>
public sealed class BmffStream : Stream
{
    private readonly Stream _stream;
    private readonly long _offset;
    private readonly long _sourceOffset;
    private readonly bool _forwardOnly;
    private ContentSliceStream? _slice;
    private long _position;
    private bool _disposed;

    /// <summary>Creates a window relative to the parent stream.</summary>
    /// <param name="stream">The borrowed parent.</param>
    /// <param name="offset">The parent-relative start; its current cursor for forward-only parents.</param>
    /// <param name="limit">The byte length of the window.</param>
    /// <exception cref="ArgumentNullException">The parent is null.</exception>
    /// <exception cref="ArgumentOutOfRangeException">The range is invalid or exceeds a parent window.</exception>
    public BmffStream(Stream stream, long offset, long limit)
    {
        ArgumentNullException.ThrowIfNull(stream);
        ArgumentOutOfRangeException.ThrowIfNegative(offset);
        ArgumentOutOfRangeException.ThrowIfNegative(limit);
        _stream = stream;
        _sourceOffset = offset;
        _forwardOnly = !stream.CanSeek;
        var origin = stream is BmffStream bmff ? bmff.Offset : stream is ContentSliceStream slice ? slice.SourceOffset : 0;
        ArgumentOutOfRangeException.ThrowIfGreaterThan(offset, long.MaxValue - origin);
        _offset = origin + offset;
        ArgumentOutOfRangeException.ThrowIfGreaterThan(limit, long.MaxValue - Offset);
        if (stream is BmffStream parent)
        {
            Options = parent.Options;
            Registry = parent.Registry;
            Depth = parent.Depth;
            CancellationToken = parent.CancellationToken;
            HasKnownLength = parent.HasKnownLength;
            ArgumentOutOfRangeException.ThrowIfGreaterThan(offset, parent.Length);
            ArgumentOutOfRangeException.ThrowIfGreaterThan(limit, parent.Length - offset);
            if (_forwardOnly)
            {
                ArgumentOutOfRangeException.ThrowIfNotEqual(offset, parent.Position);
            }
        }
        Limit = limit;
    }

    internal BmffBoxType ChildContext { get; init; }
    internal long AllocatedFieldBytes { get; set; }
    internal BmffWritePlan? WritePlan { get; init; }
    internal long AbsoluteOrigin { get; init; }
    internal BmffReaderOptions Options { get; init; } = new();
    internal BmffBoxRegistry Registry { get; init; } = BmffBoxRegistry.Default;
    internal int Depth { get; init; }
    internal bool HasKnownLength { get; init; } = true;
    internal CancellationToken CancellationToken { get; set; }

    /// <summary>Enumerates child headers in the remaining payload using the enclosing reader's limits.</summary>
    /// <returns>A caller-owned reader borrowing this window.</returns>
    /// <exception cref="ContentFormatException">The depth limit is exceeded.</exception>
    public BmffReader ReadChildren() => ReadChildrenCore(null);

    internal BmffReader ReadChildrenCore(int? expectedCount)
    {
        if (Depth >= Options.MaxDepth && Remaining != 0)
        {
            throw new ContentFormatException(ErrorMessages.DepthLimit, ContentFormatErrorCode.LimitExceeded, Offset + Position);
        }
        var window = new BmffStream(this, Position, Remaining) { Depth = Depth + 1, ChildContext = BoxType };
        return new BmffReaderDefault(window, false, expectedCount);
    }

    internal BinarySegment GetSegment(long offset, long length)
    {
        if (!CanSeek)
        {
            throw new ContentFormatException(ErrorMessages.LazySourceRequired, ContentFormatErrorCode.Unsupported, Offset + offset);
        }
        return _stream is BmffStream parent
            ? parent.GetSegment(_sourceOffset + offset, length)
            : BinarySegment.Create(_stream, _sourceOffset + offset, length);
    }

    /// <summary>Gets or sets the contextual box type.</summary>
    public BmffBoxType BoxType { get; set; }
    /// <summary>Gets the absolute start of the window.</summary>
    public long Offset => _offset + AbsoluteOrigin;
    /// <summary>Gets the allowed byte length.</summary>
    public long Limit { get; }
    /// <summary>Gets the bytes remaining in this window.</summary>
    public long Remaining => Math.Max(0, Limit - Position);
    /// <inheritdoc/>
    public override bool CanRead => !_disposed && _stream.CanRead;
    /// <inheritdoc/>
    public override bool CanSeek => !_disposed && !_forwardOnly;
    /// <inheritdoc/>
    public override bool CanWrite => !_disposed && _stream.CanWrite;
    /// <inheritdoc/>
    public override long Length
    {
        get { ObjectDisposedException.ThrowIf(_disposed, this); return Limit; }
    }
    /// <inheritdoc/>
    public override long Position
    {
        get { ObjectDisposedException.ThrowIf(_disposed, this); return _position; }
        set { Seek(value, SeekOrigin.Begin); }
    }
    /// <inheritdoc/>
    public override void Flush()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        _stream.Flush();
    }
    /// <inheritdoc/>
    public override int Read(byte[] buffer, int offset, int count)
    {
        ArgumentNullException.ThrowIfNull(buffer);
        return Read(buffer.AsSpan(offset, count));
    }
    private ContentSliceStream GetSlice()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_slice is null)
        {
            _slice = _forwardOnly
                ? ContentSliceStream.CreateForwardOnly(_stream, Limit)
                : ContentSliceStream.Create(_stream, Math.Min(_sourceOffset, _stream.Length), Math.Min(Limit, Math.Max(0, _stream.Length - _sourceOffset)));
        }
        if (!_forwardOnly)
        {
            _slice.Position = _position;
        }
        return _slice;
    }
    /// <inheritdoc/>
    public override int Read(Span<byte> buffer)
    {
        CancellationToken.ThrowIfCancellationRequested();
        var count = GetSlice().Read(buffer[..(int)Math.Min(buffer.Length, Remaining)]);
        _position += count;
        return count;
    }
    /// <inheritdoc/>
    public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
    {
        var count = await GetSlice().ReadAsync(buffer[..(int)Math.Min(buffer.Length, Remaining)], cancellationToken).ConfigureAwait(false);
        _position += count;
        return count;
    }
    /// <inheritdoc/>
    public override long Seek(long offset, SeekOrigin origin)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (!CanSeek)
        {
            throw new NotSupportedException(ErrorMessages.StreamNotSeekable);
        }
        var start = origin switch
        {
            SeekOrigin.Begin => 0,
            SeekOrigin.Current => _position,
            SeekOrigin.End => Limit,
            _ => throw new ArgumentException(ErrorMessages.InvalidSeekOrigin, nameof(origin))
        };
        if (offset < -start || offset > long.MaxValue - start)
        {
            throw new IOException(ErrorMessages.InvalidSeek);
        }
        var target = start + offset;
        if (target > long.MaxValue - Offset)
        {
            throw new IOException(ErrorMessages.InvalidSeek);
        }
        _stream.Position = _sourceOffset + target;
        _position = target;
        return _position;
    }
    /// <inheritdoc/>
    public override void SetLength(long value) => throw new NotSupportedException(ErrorMessages.FixedWindow);
    /// <inheritdoc/>
    public override void Write(byte[] buffer, int offset, int count)
    {
        ArgumentNullException.ThrowIfNull(buffer);
        Write(buffer.AsSpan(offset, count));
    }
    private void PrepareWrite(int count)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (count > Remaining)
        {
            throw new ContentFormatException(ErrorMessages.WindowExceeded, ContentFormatErrorCode.InvalidStructure, Offset + Position);
        }
        if (!_forwardOnly)
        {
            _slice?.Dispose();
            _slice = null;
            _stream.Position = _sourceOffset + _position;
        }
    }
    /// <inheritdoc/>
    public override void Write(ReadOnlySpan<byte> buffer)
    {
        CancellationToken.ThrowIfCancellationRequested();
        PrepareWrite(buffer.Length);
        _stream.Write(buffer);
        _position += buffer.Length;
    }
    /// <inheritdoc/>
    public override async ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
    {
        PrepareWrite(buffer.Length);
        await _stream.WriteAsync(buffer, cancellationToken).ConfigureAwait(false);
        _position += buffer.Length;
    }
    /// <inheritdoc/>
    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _slice?.Dispose();
        }
        _disposed = true;
        base.Dispose(disposing);
    }
}

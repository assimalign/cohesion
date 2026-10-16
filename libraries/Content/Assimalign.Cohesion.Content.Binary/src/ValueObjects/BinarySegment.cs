using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;

namespace Assimalign.Cohesion.Content.Binary;

/// <summary>A payload stored in memory or borrowed as a lazy range of a seekable source.</summary>
/// <remarks>Default is the empty materialized segment. Lazy segments never own their source: it must remain
/// open, its byte layout unchanged, and its cursor unmoved by concurrent operations. Sequential reads
/// reposition it. Equality compares backing-memory identity or source identity and range, not byte contents.</remarks>
public readonly struct BinarySegment : IEquatable<BinarySegment>
{
    private readonly ReadOnlyMemory<byte> _memory;
    private readonly Stream? _source;
    private readonly long _offset;
    private readonly long _length;

    private BinarySegment(ReadOnlyMemory<byte> memory, Stream? source, long offset, long length)
    {
        _memory = memory;
        _source = source;
        _offset = offset;
        _length = length;
    }

    /// <summary>Creates a materialized segment borrowing the supplied bytes.</summary>
    /// <param name="data">The bytes; callers must not mutate them while the segment is used.</param>
    /// <returns>A materialized segment, or default for empty memory.</returns>
    public static BinarySegment Create(ReadOnlyMemory<byte> data) =>
        data.IsEmpty ? default : new BinarySegment(data, null, 0, data.Length);

    /// <summary>Creates a lazy segment without reading or allocating the payload.</summary>
    /// <param name="source">The readable, seekable source, borrowed for the segment's lifetime.</param>
    /// <param name="offset">The nonnegative start in source coordinates.</param>
    /// <param name="length">The nonnegative byte count.</param>
    /// <returns>A lazy segment, or default for an empty range.</returns>
    /// <exception cref="ArgumentNullException">The source is null.</exception>
    /// <exception cref="ArgumentException">The source is not readable and seekable.</exception>
    /// <exception cref="ArgumentOutOfRangeException">The range does not fit in the source.</exception>
    public static BinarySegment Create(Stream source, long offset, long length)
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
        return length == 0 ? default : new BinarySegment(default, source, offset, length);
    }

    /// <summary>Gets the payload length without materializing it.</summary>
    public long Length => _length;

    /// <summary>Gets whether the segment contains no bytes.</summary>
    public bool IsEmpty => _length == 0;

    /// <summary>Gets whether the bytes are already in memory, including the default empty segment.</summary>
    public bool IsMaterialized => _source is null;

    /// <summary>Tries to borrow the materialized bytes without reading a lazy source.</summary>
    /// <param name="memory">The materialized bytes, or empty memory for a lazy segment.</param>
    /// <returns>Whether this segment is materialized.</returns>
    public bool TryGetMemory(out ReadOnlyMemory<byte> memory)
    {
        memory = _memory;
        return IsMaterialized;
    }

    /// <summary>Opens a caller-owned read-only stream over the payload.</summary>
    /// <returns>A borrowed-source ContentSliceStream or a read-only MemoryStream.</returns>
    /// <remarks>Array-backed memory is shared without copying; other memory is copied for the MemoryStream.
    /// Disposing the returned stream never disposes a lazy segment's source.</remarks>
    /// <exception cref="ArgumentException">The lazy source is no longer readable and seekable, including after disposal.</exception>
    /// <exception cref="ArgumentOutOfRangeException">The lazy source no longer contains the recorded range.</exception>
    /// <exception cref="System.ObjectDisposedException">The borrowed source has been disposed.</exception>
    /// <exception cref="System.NotSupportedException">A borrowed stream no longer supports the required read, seek or write operation.</exception>
    /// <exception cref="System.IO.IOException">Reading or writing the underlying stream fails.</exception>
    public Stream OpenRead()
    {
        if (_source is not null)
        {
            return ContentSliceStream.Create(_source, _offset, _length);
        }
        if (MemoryMarshal.TryGetArray(_memory, out var array) && array.Array is not null)
        {
            return new MemoryStream(array.Array, array.Offset, array.Count, writable: false, publiclyVisible: false);
        }
        return new MemoryStream(_memory.ToArray(), writable: false);
    }

    /// <summary>Copies the payload through without materializing a lazy range.</summary>
    /// <param name="destination">The caller-owned destination stream.</param>
    /// <exception cref="ArgumentNullException">The destination is null.</exception>
    /// <exception cref="ContentFormatException">The lazy source is truncated during the copy.</exception>
    /// <exception cref="System.ObjectDisposedException">The borrowed source has been disposed.</exception>
    /// <exception cref="System.NotSupportedException">A borrowed stream no longer supports the required read, seek or write operation.</exception>
    /// <exception cref="System.IO.IOException">Reading or writing the underlying stream fails.</exception>
    public void CopyTo(Stream destination)
    {
        ArgumentNullException.ThrowIfNull(destination);
        if (IsEmpty)
        {
            return;
        }
        if (IsMaterialized)
        {
            destination.Write(_memory.Span);
            return;
        }
        using var source = OpenRead();
        source.CopyExactlyTo(destination, _length);
    }

    /// <summary>Copies the payload asynchronously without materializing a lazy range.</summary>
    /// <param name="destination">The caller-owned destination stream.</param>
    /// <param name="cancellationToken">A token used to cancel the operation.</param>
    /// <returns>A task completing when all bytes have been copied.</returns>
    /// <exception cref="ArgumentNullException">The destination is null.</exception>
    /// <exception cref="ContentFormatException">The lazy source is truncated during the copy.</exception>
    /// <exception cref="OperationCanceledException">The nonempty operation is canceled.</exception>
    /// <exception cref="System.ObjectDisposedException">The borrowed source has been disposed.</exception>
    /// <exception cref="System.NotSupportedException">A borrowed stream no longer supports the required read, seek or write operation.</exception>
    /// <exception cref="System.IO.IOException">Reading or writing the underlying stream fails.</exception>
    public async ValueTask CopyToAsync(Stream destination, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(destination);
        if (IsEmpty)
        {
            return;
        }
        if (IsMaterialized)
        {
            await destination.WriteAsync(_memory, cancellationToken).ConfigureAwait(false);
            return;
        }
        await using var source = OpenRead();
        await source.CopyExactlyToAsync(destination, _length, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Creates a subrange without reading or copying its bytes.</summary>
    /// <param name="offset">The nonnegative start relative to this segment.</param>
    /// <param name="length">The nonnegative length.</param>
    /// <returns>The requested subrange, or default for an empty range.</returns>
    /// <exception cref="ArgumentOutOfRangeException">The subrange does not fit.</exception>
    public BinarySegment Slice(long offset, long length)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(offset);
        ArgumentOutOfRangeException.ThrowIfNegative(length);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(offset, _length);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(length, _length - offset);
        if (length == 0)
        {
            return default;
        }
        return IsMaterialized
            ? Create(_memory.Slice((int)offset, (int)length))
            : new BinarySegment(default, _source, _offset + offset, length);
    }

    /// <summary>Materializes a copy only after checking the caller's explicit size cap.</summary>
    /// <param name="maxLength">The nonnegative maximum number of bytes permitted.</param>
    /// <returns>A copy of the payload.</returns>
    /// <exception cref="ArgumentOutOfRangeException">The maximum length is negative.</exception>
    /// <exception cref="ContentFormatException">The length exceeds the cap or maximum array length (LimitExceeded), or the source is truncated (Truncated).</exception>
    /// <exception cref="System.ObjectDisposedException">The borrowed source has been disposed.</exception>
    /// <exception cref="System.NotSupportedException">A borrowed stream no longer supports the required read, seek or write operation.</exception>
    /// <exception cref="System.IO.IOException">Reading or writing the underlying stream fails.</exception>
    public byte[] ToArray(int maxLength)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(maxLength);
        if (_length > maxLength || _length > Array.MaxLength)
        {
            throw new ContentFormatException(ErrorMessages.MaterializationLimit, ContentFormatErrorCode.LimitExceeded, _source is null ? null : _offset);
        }
        if (IsEmpty)
        {
            return Array.Empty<byte>();
        }
        if (IsMaterialized)
        {
            return _memory.ToArray();
        }
        var result = new byte[(int)_length];
        using var source = OpenRead();
        source.ReadContentExactly(result);
        return result;
    }

    /// <inheritdoc/>
    public bool Equals(BinarySegment other) =>
        ReferenceEquals(_source, other._source) && _offset == other._offset && _length == other._length && _memory.Equals(other._memory);

    /// <inheritdoc/>
    public override bool Equals(object? obj) => obj is BinarySegment other && Equals(other);

    /// <inheritdoc/>
    public override int GetHashCode() => HashCode.Combine(_source, _offset, _length, _memory);
}

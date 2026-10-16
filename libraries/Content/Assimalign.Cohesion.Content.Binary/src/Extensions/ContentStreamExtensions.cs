using System;
using System.Buffers;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

namespace Assimalign.Cohesion.Content.Binary;

/// <summary>Exact-count stream operations for binary parsers.</summary>
/// <remarks>Zero-length operations succeed without accessing the stream. Shortfalls report Truncated at
/// the stream's current Position, or a forward-only slice's tracked Position; other non-seekable sources
/// have no position. These are the supplied stream's coordinates (window-relative for slices).</remarks>
public static class ContentStreamExtensions
{
    extension(Stream stream)
    {
        /// <summary>Fills the buffer, returning false only for clean end before its first byte.</summary>
        /// <param name="buffer">The buffer to fill; an empty buffer succeeds.</param>
        /// <returns>True when filled, or false for clean end.</returns>
        /// <exception cref="ArgumentNullException">The stream is null.</exception>
        /// <exception cref="ContentFormatException">The stream ends after returning part of the buffer.</exception>
        public bool TryReadExactly(Span<byte> buffer)
        {
            ArgumentNullException.ThrowIfNull(stream);
            var read = 0;
            while (read < buffer.Length)
            {
                var count = stream.Read(buffer[read..]);
                if (count == 0)
                {
                    if (read == 0)
                    {
                        return false;
                    }
                    throw new ContentFormatException(ErrorMessages.TruncatedInput, ContentFormatErrorCode.Truncated, GetPosition(stream));
                }
                read += count;
            }
            return true;
        }

        /// <summary>Fills the buffer or reports truncation for any shortfall.</summary>
        /// <param name="buffer">The buffer to fill; an empty buffer succeeds.</param>
        /// <exception cref="ArgumentNullException">The stream is null.</exception>
        /// <exception cref="ContentFormatException">The stream ends before filling the buffer.</exception>
        public void ReadContentExactly(Span<byte> buffer)
        {
            if (!(stream.TryReadExactly(buffer)))
            {
                throw new ContentFormatException(ErrorMessages.TruncatedInput, ContentFormatErrorCode.Truncated, GetPosition(stream));
            }
        }

        /// <summary>Skips exactly the requested count without buffering the entire range.</summary>
        /// <param name="count">The nonnegative byte count; zero does not access either stream.</param>
        /// <exception cref="ArgumentNullException">A required stream is null.</exception>
        /// <exception cref="ArgumentOutOfRangeException">The count is negative.</exception>
        /// <exception cref="ContentFormatException">The source ends before the requested count.</exception>
        public void SkipExactly(long count)
        {
            ArgumentNullException.ThrowIfNull(stream);
            ArgumentOutOfRangeException.ThrowIfNegative(count);
            if (count == 0)
            {
                return;
            }
            if (stream.CanSeek)
            {
                var position = stream.Position;
                var length = stream.Length;
                if (position >= 0 && position <= length && count <= length - position)
                {
                    stream.Seek(count, SeekOrigin.Current);
                    return;
                }
            }
            var buffer = ArrayPool<byte>.Shared.Rent((int)Math.Min(count, 81920));
            try
            {
                while (count > 0)
                {
                    var size = (int)Math.Min(count, buffer.Length);
                    var read = stream.Read(buffer.AsSpan(0, size));
                    if (read == 0)
                    {
                        throw new ContentFormatException(ErrorMessages.TruncatedInput, ContentFormatErrorCode.Truncated, GetPosition(stream));
                    }
                    count -= read;
                }
            }
            finally
            {
                ArrayPool<byte>.Shared.Return(buffer);
            }
        }

        /// <summary>Copies exactly the requested count without buffering the entire range.</summary>
        /// <param name="destination">The caller-owned destination.</param>
        /// <param name="count">The nonnegative byte count; zero does not access either stream.</param>
        /// <exception cref="ArgumentNullException">A required stream is null.</exception>
        /// <exception cref="ArgumentOutOfRangeException">The count is negative.</exception>
        /// <exception cref="ContentFormatException">The source ends before the requested count.</exception>
        public void CopyExactlyTo(Stream destination, long count)
        {
            ArgumentNullException.ThrowIfNull(stream);
            ArgumentNullException.ThrowIfNull(destination);
            ArgumentOutOfRangeException.ThrowIfNegative(count);
            if (count == 0)
            {
                return;
            }
            var buffer = ArrayPool<byte>.Shared.Rent((int)Math.Min(count, 81920));
            try
            {
                while (count > 0)
                {
                    var size = (int)Math.Min(count, buffer.Length);
                    var read = stream.Read(buffer.AsSpan(0, size));
                    if (read == 0)
                    {
                        throw new ContentFormatException(ErrorMessages.TruncatedInput, ContentFormatErrorCode.Truncated, GetPosition(stream));
                    }
                    destination.Write(buffer.AsSpan(0, read));
                    count -= read;
                }
            }
            finally
            {
                ArrayPool<byte>.Shared.Return(buffer);
            }
        }

        /// <summary>Fills the buffer, returning false only for clean end before its first byte.</summary>
        /// <param name="buffer">The buffer to fill; an empty buffer succeeds.</param>
        /// <param name="cancellationToken">A token used to cancel the operation.</param>
        /// <returns>True when filled, or false for clean end.</returns>
        /// <exception cref="ArgumentNullException">The stream is null.</exception>
        /// <exception cref="ContentFormatException">The stream ends after returning part of the buffer.</exception>
        /// <exception cref="OperationCanceledException">The nonempty operation is canceled.</exception>
        public async ValueTask<bool> TryReadExactlyAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            ArgumentNullException.ThrowIfNull(stream);
            var read = 0;
            while (read < buffer.Length)
            {
                var count = await stream.ReadAsync(buffer[read..], cancellationToken).ConfigureAwait(false);
                if (count == 0)
                {
                    if (read == 0)
                    {
                        return false;
                    }
                    throw new ContentFormatException(ErrorMessages.TruncatedInput, ContentFormatErrorCode.Truncated, GetPosition(stream));
                }
                read += count;
            }
            return true;
        }

        /// <summary>Fills the buffer or reports truncation for any shortfall.</summary>
        /// <param name="buffer">The buffer to fill; an empty buffer succeeds.</param>
        /// <param name="cancellationToken">A token used to cancel the operation.</param>
        /// <returns>A task completing when the buffer has been filled.</returns>
        /// <exception cref="ArgumentNullException">The stream is null.</exception>
        /// <exception cref="ContentFormatException">The stream ends before filling the buffer.</exception>
        /// <exception cref="OperationCanceledException">The nonempty operation is canceled.</exception>
        public async ValueTask ReadContentExactlyAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            if (!(await stream.TryReadExactlyAsync(buffer, cancellationToken).ConfigureAwait(false)))
            {
                throw new ContentFormatException(ErrorMessages.TruncatedInput, ContentFormatErrorCode.Truncated, GetPosition(stream));
            }
        }

        /// <summary>Skips exactly the requested count without buffering the entire range.</summary>
        /// <param name="count">The nonnegative byte count; zero does not access either stream.</param>
        /// <param name="cancellationToken">A token used to cancel the operation.</param>
        /// <returns>A task completing when the requested bytes have been processed.</returns>
        /// <exception cref="ArgumentNullException">A required stream is null.</exception>
        /// <exception cref="ArgumentOutOfRangeException">The count is negative.</exception>
        /// <exception cref="ContentFormatException">The source ends before the requested count.</exception>
        /// <exception cref="OperationCanceledException">The nonempty operation is canceled.</exception>
        public async ValueTask SkipExactlyAsync(long count, CancellationToken cancellationToken = default)
        {
            ArgumentNullException.ThrowIfNull(stream);
            ArgumentOutOfRangeException.ThrowIfNegative(count);
            if (count == 0)
            {
                return;
            }
            cancellationToken.ThrowIfCancellationRequested();
            if (stream.CanSeek)
            {
                var position = stream.Position;
                var length = stream.Length;
                if (position >= 0 && position <= length && count <= length - position)
                {
                    stream.Seek(count, SeekOrigin.Current);
                    return;
                }
            }
            var buffer = ArrayPool<byte>.Shared.Rent((int)Math.Min(count, 81920));
            try
            {
                while (count > 0)
                {
                    var size = (int)Math.Min(count, buffer.Length);
                    var read = await stream.ReadAsync(buffer.AsMemory(0, size), cancellationToken).ConfigureAwait(false);
                    if (read == 0)
                    {
                        throw new ContentFormatException(ErrorMessages.TruncatedInput, ContentFormatErrorCode.Truncated, GetPosition(stream));
                    }
                    count -= read;
                }
            }
            finally
            {
                ArrayPool<byte>.Shared.Return(buffer);
            }
        }

        /// <summary>Copies exactly the requested count without buffering the entire range.</summary>
        /// <param name="destination">The caller-owned destination.</param>
        /// <param name="count">The nonnegative byte count; zero does not access either stream.</param>
        /// <param name="cancellationToken">A token used to cancel the operation.</param>
        /// <returns>A task completing when the requested bytes have been processed.</returns>
        /// <exception cref="ArgumentNullException">A required stream is null.</exception>
        /// <exception cref="ArgumentOutOfRangeException">The count is negative.</exception>
        /// <exception cref="ContentFormatException">The source ends before the requested count.</exception>
        /// <exception cref="OperationCanceledException">The nonempty operation is canceled.</exception>
        public async ValueTask CopyExactlyToAsync(Stream destination, long count, CancellationToken cancellationToken = default)
        {
            ArgumentNullException.ThrowIfNull(stream);
            ArgumentNullException.ThrowIfNull(destination);
            ArgumentOutOfRangeException.ThrowIfNegative(count);
            if (count == 0)
            {
                return;
            }
            cancellationToken.ThrowIfCancellationRequested();
            var buffer = ArrayPool<byte>.Shared.Rent((int)Math.Min(count, 81920));
            try
            {
                while (count > 0)
                {
                    var size = (int)Math.Min(count, buffer.Length);
                    var read = await stream.ReadAsync(buffer.AsMemory(0, size), cancellationToken).ConfigureAwait(false);
                    if (read == 0)
                    {
                        throw new ContentFormatException(ErrorMessages.TruncatedInput, ContentFormatErrorCode.Truncated, GetPosition(stream));
                    }
                    await destination.WriteAsync(buffer.AsMemory(0, read), cancellationToken).ConfigureAwait(false);
                    count -= read;
                }
            }
            finally
            {
                ArrayPool<byte>.Shared.Return(buffer);
            }
        }

    }

    private static long? GetPosition(Stream stream) =>
        stream.CanSeek || stream is ContentSliceStream ? stream.Position : null;
}

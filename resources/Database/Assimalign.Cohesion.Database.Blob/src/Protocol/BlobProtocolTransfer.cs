using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Assimalign.Cohesion.Database.Protocol;

namespace Assimalign.Cohesion.Database.Blob;

/// <summary>Transfers Blob content between caller-owned streams and a Blob protocol channel.</summary>
/// <remarks>
/// These helpers implement TransferStart, Chunk, ChunkAcknowledgement, and TransferComplete after the shared handshake
/// and a Blob request. They do not open connections, issue requests, acknowledge upload publication,
/// or own the channel or content streams. Only one exchange may use the channel at a time.
/// After failure or cancellation, discard the channel and abort any partially written destination.
/// </remarks>
public static class BlobProtocolTransfer
{
    /// <summary>Sends content with one reusable buffer and awaits acceptance of every chunk.</summary>
    /// <param name="channel">A channel bound to <see cref="BlobProtocol.Family"/>.</param>
    /// <param name="source">The readable content stream, which need not support seeking.</param>
    /// <param name="metadata">The expected length and content type.</param>
    /// <param name="cancellationToken">Cancellation token for content and frame reads, writes, and flushes.</param>
    /// <returns>The actual content byte count after writing the completion frame.</returns>
    /// <exception cref="ArgumentNullException">A required argument is null.</exception>
    /// <exception cref="ArgumentException">The channel family or stream access is incorrect.</exception>
    /// <exception cref="ProtocolException">The metadata, source length, or peer acknowledgement is invalid.</exception>
    /// <exception cref="OperationCanceledException">The operation is canceled.</exception>
    public static async ValueTask<long> SendAsync(
        ProtocolChannel channel,
        Stream source,
        BlobTransferStartMessage metadata,
        CancellationToken cancellationToken = default)
    {
        ValidateChannel(channel);
        return await SendAsync(channel.Reader, channel.Writer, source, metadata, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Sends content through the frame endpoints of an exclusive Blob exchange.</summary>
    /// <param name="reader">The Blob-bound frame reader.</param>
    /// <param name="writer">The Blob-bound frame writer.</param>
    /// <param name="source">The caller-owned readable source.</param>
    /// <param name="metadata">The expected length and content type.</param>
    /// <param name="cancellationToken">Cancellation token for the entire transfer.</param>
    /// <returns>The verified actual byte count.</returns>
    /// <exception cref="ArgumentNullException">A required argument is null.</exception>
    /// <exception cref="ArgumentException">The source is not readable.</exception>
    /// <exception cref="ProtocolException">The metadata, length or acknowledgement is invalid.</exception>
    /// <exception cref="OperationCanceledException">The transfer is canceled.</exception>
    /// <remarks>The caller must hold exclusive access to both endpoints for the entire transfer.</remarks>
    public static async ValueTask<long> SendAsync(
        IProtocolFrameReader reader, IProtocolFrameWriter writer, Stream source,
        BlobTransferStartMessage metadata, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(reader);
        ArgumentNullException.ThrowIfNull(writer);
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(metadata);
        if (!source.CanRead)
        {
            throw new ArgumentException("The content stream must be readable.", nameof(source));
        }
        await WriteAsync(writer, BlobProtocolMessageType.TransferStart, metadata.Encode(), cancellationToken).ConfigureAwait(false);
        var buffer = new byte[BlobProtocol.MaxChunkLength];
        long total = 0;
        while (true)
        {
            int read = await source.ReadAsync(buffer, cancellationToken).ConfigureAwait(false);
            if (read == 0)
            {
                break;
            }
            total = AddLength(total, read, metadata.Length);
            await writer.WriteFrameAsync(new BlobChunkMessage(buffer.AsMemory(0, read)).ToFrame(), cancellationToken).ConfigureAwait(false);
            await writer.FlushAsync(cancellationToken).ConfigureAwait(false);
            ProtocolFrame acknowledgement = await ReadAsync(reader, cancellationToken).ConfigureAwait(false);
            if (acknowledgement.Type != (ProtocolMessageType)BlobProtocolMessageType.ChunkAcknowledgement ||
                BlobChunkAcknowledgementMessage.Decode(acknowledgement.Payload.Span).Length != total)
            {
                throw new ProtocolException("The Blob chunk acknowledgement disagrees with the transferred content.");
            }
        }
        if (metadata.Length >= 0 && total != metadata.Length)
        {
            throw new ProtocolException("The Blob source ended before its declared length.");
        }
        await WriteAsync(writer, BlobProtocolMessageType.TransferComplete,
            new BlobTransferCompleteMessage(total).Encode(), cancellationToken).ConfigureAwait(false);
        return total;
    }

    /// <summary>Copies each incoming content chunk to the destination before reading the next.</summary>
    /// <param name="channel">A channel bound to <see cref="BlobProtocol.Family"/>.</param>
    /// <param name="destination">The writable destination, which need not support seeking.</param>
    /// <param name="cancellationToken">Cancellation token for frame reads, acknowledgements, and content writes.</param>
    /// <returns>The content type and actual verified byte count, resolving an initially unknown length.</returns>
    /// <exception cref="ArgumentNullException">A required argument is null.</exception>
    /// <exception cref="ArgumentException">The channel family or stream access is incorrect.</exception>
    /// <exception cref="ProtocolException">The exchange is malformed, truncated, out of order, or terminated by an error.</exception>
    /// <exception cref="OperationCanceledException">The operation is canceled.</exception>
    /// <remarks>
    /// Completion verifies the sender's length against the bytes received. It does not flush,
    /// dispose, or publish the destination. The caller owns storage commit and its acknowledgement.
    /// </remarks>
    public static async ValueTask<BlobTransferStartMessage> ReceiveAsync(
        ProtocolChannel channel,
        Stream destination,
        CancellationToken cancellationToken = default)
    {
        ValidateChannel(channel);
        return await ReceiveAsync(channel.Reader, channel.Writer, destination, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Receives a transfer through the frame endpoints of an exclusive Blob exchange.</summary>
    /// <param name="reader">The Blob-bound frame reader.</param>
    /// <param name="writer">The Blob-bound frame writer.</param>
    /// <param name="destination">The caller-owned writable destination.</param>
    /// <param name="cancellationToken">Cancellation token for the entire transfer.</param>
    /// <returns>The metadata with the verified actual byte count.</returns>
    /// <exception cref="ArgumentNullException">A required argument is null.</exception>
    /// <exception cref="ArgumentException">The destination is not writable.</exception>
    /// <exception cref="ProtocolException">The transfer is malformed, truncated or terminated by an error.</exception>
    /// <exception cref="OperationCanceledException">The transfer is canceled.</exception>
    public static async ValueTask<BlobTransferStartMessage> ReceiveAsync(
        IProtocolFrameReader reader, IProtocolFrameWriter writer, Stream destination,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(reader);
        ArgumentNullException.ThrowIfNull(writer);
        ArgumentNullException.ThrowIfNull(destination);
        if (!destination.CanWrite)
        {
            throw new ArgumentException("The content stream must be writable.", nameof(destination));
        }
        ProtocolFrame start = await ReadAsync(reader, cancellationToken).ConfigureAwait(false);
        if (start.Type != (ProtocolMessageType)BlobProtocolMessageType.TransferStart)
        {
            throw new ProtocolException("A Blob transfer must begin with TransferStart.");
        }
        BlobTransferStartMessage metadata = BlobTransferStartMessage.Decode(start.Payload.Span);
        return await ReceiveAsync(reader, writer, destination, metadata, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Receives chunks after the caller has consumed and validated TransferStart.</summary>
    /// <param name="reader">The Blob-bound frame reader positioned after TransferStart.</param>
    /// <param name="writer">The Blob-bound frame writer.</param>
    /// <param name="destination">The caller-owned writable destination opened using the metadata.</param>
    /// <param name="metadata">The already decoded TransferStart metadata.</param>
    /// <param name="cancellationToken">Cancellation token for the remaining transfer.</param>
    /// <returns>The metadata with the verified actual byte count.</returns>
    /// <exception cref="ArgumentNullException">A required argument is null.</exception>
    /// <exception cref="ArgumentException">The destination is not writable.</exception>
    /// <exception cref="ProtocolException">The transfer metadata or content is invalid.</exception>
    /// <exception cref="OperationCanceledException">The transfer is canceled.</exception>
    /// <remarks>The caller retains exclusive access and owns rollback, disposal and publication.</remarks>
    public static async ValueTask<BlobTransferStartMessage> ReceiveAsync(
        IProtocolFrameReader reader, IProtocolFrameWriter writer, Stream destination,
        BlobTransferStartMessage metadata, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(reader);
        ArgumentNullException.ThrowIfNull(writer);
        ArgumentNullException.ThrowIfNull(destination);
        ArgumentNullException.ThrowIfNull(metadata);
        if (!destination.CanWrite)
        {
            throw new ArgumentException("The content stream must be writable.", nameof(destination));
        }
        if (metadata.Length < -1)
        {
            throw new ProtocolException("A Blob transfer length must be nonnegative or -1.");
        }

        long total = 0;
        while (true)
        {
            ProtocolFrame frame = await ReadAsync(reader, cancellationToken).ConfigureAwait(false);
            switch ((BlobProtocolMessageType)frame.Type)
            {
                case BlobProtocolMessageType.Chunk:
                    BlobChunkMessage chunk = BlobChunkMessage.Decode(frame.Payload);
                    total = AddLength(total, chunk.Content.Length, metadata.Length);
                    await destination.WriteAsync(chunk.Content, cancellationToken).ConfigureAwait(false);
                    await WriteAsync(writer, BlobProtocolMessageType.ChunkAcknowledgement,
                        new BlobChunkAcknowledgementMessage(total).Encode(), cancellationToken).ConfigureAwait(false);
                    break;
                case BlobProtocolMessageType.TransferComplete:
                    long completed = BlobTransferCompleteMessage.Decode(frame.Payload.Span).Length;
                    if (completed != total || (metadata.Length >= 0 && metadata.Length != total))
                    {
                        throw new ProtocolException("The Blob completion length disagrees with the transferred content.");
                    }
                    return metadata with { Length = total };
                default:
                    throw new ProtocolException($"Unexpected message {(byte)frame.Type} during a Blob transfer.");
            }
        }
    }

    private static void ValidateChannel(ProtocolChannel channel)
    {
        ArgumentNullException.ThrowIfNull(channel);
        if (!ReferenceEquals(channel.Family, BlobProtocol.Family))
        {
            throw new ArgumentException("The channel must be bound to BlobProtocol.Family.", nameof(channel));
        }
    }

    private static long AddLength(long total, int count, long expected)
    {
        if (count > long.MaxValue - total || (expected >= 0 && count > expected - total))
        {
            throw new ProtocolException("The Blob content exceeds its declared or supported length.");
        }
        return total + count;
    }

    private static async ValueTask<ProtocolFrame> ReadAsync(IProtocolFrameReader reader, CancellationToken cancellationToken)
    {
        ProtocolFrame? frame = await reader.ReadFrameAsync(cancellationToken).ConfigureAwait(false);
        if (frame is null)
        {
            throw new ProtocolException("The connection ended before the Blob transfer completed.");
        }
        if (frame.Value.Type == ProtocolMessageType.Error)
        {
            ProtocolErrorMessage error = ProtocolErrorMessage.Decode(frame.Value.Payload.Span);
            throw new ProtocolException($"Blob transfer failed ({error.Code}): {error.Message}");
        }
        return frame.Value;
    }

    private static async ValueTask WriteAsync(IProtocolFrameWriter writer, BlobProtocolMessageType type,
        ReadOnlyMemory<byte> payload, CancellationToken cancellationToken)
    {
        await writer.WriteFrameAsync(new((ProtocolMessageType)type, payload), cancellationToken).ConfigureAwait(false);
        await writer.FlushAsync(cancellationToken).ConfigureAwait(false);
    }
}

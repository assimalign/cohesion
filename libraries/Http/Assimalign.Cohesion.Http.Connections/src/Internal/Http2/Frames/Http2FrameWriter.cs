using System;
using System.Buffers;
using System.Buffers.Binary;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

using Assimalign.Cohesion.Http.Internal;

namespace Assimalign.Cohesion.Http.Connections.Internal;

/// <summary>
/// Serializes outbound HTTP/2 frames (RFC 9113 §4.1) onto the connection's stream, each frame — and
/// each header block — in a single write.
/// </summary>
/// <remarks>
/// <para>
/// A write is the unit a cancellation can interrupt: the connection's pipe hands a write's octets to
/// the transport before it waits for room, and a cancellation cuts the wait short, not the copy. A
/// frame written as a header write and a payload write could therefore be cut between the two: the
/// header would be on the wire claiming a payload that never follows, and every later frame on the
/// connection would be misread. Writing the whole frame at once means a cancellation is observed only
/// before the frame starts or after its last octet is handed over (#1326).
/// </para>
/// <para>
/// A header block is the same unit one level up: RFC 9113 §6.10 lets no other frame come between a
/// HEADERS frame and its CONTINUATION frames, so <see cref="WriteHeaderBlockAsync"/> writes the HEADERS
/// frame and every CONTINUATION frame in one write too.
/// </para>
/// </remarks>
internal static class Http2FrameWriter
{
    /// <summary>
    /// Writes one frame — its header, the fixed payload fields its type carries, and
    /// <paramref name="payload"/> — in a single write.
    /// </summary>
    /// <param name="stream">The connection's stream.</param>
    /// <param name="frame">The frame; its <see cref="Http2Frame.PayloadLength"/> is set here.</param>
    /// <param name="payload">The frame's payload after its fixed fields.</param>
    /// <param name="cancellationToken">
    /// A token observed only before the frame starts or after it was handed to the transport.
    /// </param>
    /// <returns>A task that completes once the frame is handed to the transport.</returns>
    public static async Task WriteAsync(Stream stream, Http2Frame frame, ReadOnlyMemory<byte> payload, CancellationToken cancellationToken = default)
    {
        int extendedHeaderLength = Http2FrameReader.GetPayloadFieldsLength(frame);
        int payloadLength = checked(extendedHeaderLength + payload.Length);
        frame.PayloadLength = payloadLength;

        int length = Http2FrameReader.HeaderLength + payloadLength;
        byte[] buffer = ArrayPool<byte>.Shared.Rent(length);

        try
        {
            Span<byte> destination = buffer.AsSpan(0, length);
            WriteFrameHeader(frame, destination);

            if (extendedHeaderLength > 0)
            {
                WritePayloadFields(frame, destination.Slice(Http2FrameReader.HeaderLength, extendedHeaderLength));
            }

            payload.Span.CopyTo(destination.Slice(Http2FrameReader.HeaderLength + extendedHeaderLength));
            await stream.WriteAsync(buffer.AsMemory(0, length), cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }

    /// <summary>
    /// Writes a header block (RFC 9113 §4.3) in a single write: a HEADERS frame carrying the first
    /// <paramref name="maxFrameSize"/> octets of <paramref name="headerBlock"/>, then CONTINUATION
    /// frames for the rest, <c>END_HEADERS</c> on the last frame. An empty block is one HEADERS frame.
    /// </summary>
    /// <param name="stream">The connection's stream.</param>
    /// <param name="streamId">The stream the block belongs to.</param>
    /// <param name="headerBlock">The HPACK-encoded field section.</param>
    /// <param name="endStream">Whether the HEADERS frame carries <c>END_STREAM</c>.</param>
    /// <param name="maxFrameSize">The peer's <c>SETTINGS_MAX_FRAME_SIZE</c>.</param>
    /// <param name="cancellationToken">
    /// A token observed only before the block starts or after it was handed to the transport.
    /// </param>
    /// <returns>A task that completes once the whole block is handed to the transport.</returns>
    public static async Task WriteHeaderBlockAsync(
        Stream stream,
        int streamId,
        ReadOnlyMemory<byte> headerBlock,
        bool endStream,
        int maxFrameSize,
        CancellationToken cancellationToken = default)
    {
        int frameCount = Math.Max(1, (headerBlock.Length + maxFrameSize - 1) / maxFrameSize);
        int length = checked((frameCount * Http2FrameReader.HeaderLength) + headerBlock.Length);
        byte[] buffer = ArrayPool<byte>.Shared.Rent(length);

        try
        {
            Span<byte> destination = buffer.AsSpan(0, length);
            ReadOnlySpan<byte> source = headerBlock.Span;
            Http2Frame frame = new();

            for (int index = 0; index < frameCount; index++)
            {
                int chunk = Math.Min(maxFrameSize, source.Length);
                bool last = index == frameCount - 1;

                if (index == 0)
                {
                    Http2HeadersFrameFlags flags = last ? Http2HeadersFrameFlags.EndHeaders : Http2HeadersFrameFlags.None;

                    if (endStream)
                    {
                        flags |= Http2HeadersFrameFlags.EndStream;
                    }

                    frame.PrepareHeaders(flags, streamId);
                }
                else
                {
                    frame.Type = Http2FrameType.Continuation;
                    frame.Flags = last ? (byte)Http2HeadersFrameFlags.EndHeaders : (byte)0;
                    frame.StreamId = streamId;
                }

                frame.PayloadLength = chunk;
                WriteFrameHeader(frame, destination);
                source[..chunk].CopyTo(destination[Http2FrameReader.HeaderLength..]);

                destination = destination[(Http2FrameReader.HeaderLength + chunk)..];
                source = source[chunk..];
            }

            await stream.WriteAsync(buffer.AsMemory(0, length), cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }

    private static void WriteFrameHeader(Http2Frame frame, Span<byte> destination)
    {
        Bitshifter.WriteUInt24BigEndian(destination, (uint)frame.PayloadLength);
        destination[3] = (byte)frame.Type;
        destination[4] = frame.Flags;
        Bitshifter.WriteUInt31BigEndian(destination.Slice(5, 4), (uint)frame.StreamId, preserveHighestBit: false);
    }

    private static void WritePayloadFields(Http2Frame frame, Span<byte> destination)
    {
        switch (frame.Type)
        {
            case Http2FrameType.Data:
                if (frame.DataHasPadding)
                {
                    destination[0] = frame.DataPadLength;
                }
                break;
            case Http2FrameType.Headers:
                int offset = 0;

                if (frame.HeadersHasPadding)
                {
                    destination[offset++] = frame.HeadersPadLength;
                }

                if (frame.HeadersHasPriority)
                {
                    Bitshifter.WriteUInt31BigEndian(destination.Slice(offset, 4), (uint)frame.HeadersStreamDependency, preserveHighestBit: false);
                    offset += 4;
                    destination[offset] = frame.HeadersPriorityWeight;
                }
                break;
            case Http2FrameType.GoAway:
                Bitshifter.WriteUInt31BigEndian(destination.Slice(0, 4), (uint)frame.GoAwayLastStreamId, preserveHighestBit: false);
                BinaryPrimitives.WriteUInt32BigEndian(destination.Slice(4, 4), (uint)frame.GoAwayErrorCode);
                break;
            case Http2FrameType.RstStream:
                BinaryPrimitives.WriteUInt32BigEndian(destination, (uint)frame.RstStreamErrorCode);
                break;
            case Http2FrameType.WindowUpdate:
                Bitshifter.WriteUInt31BigEndian(destination, (uint)frame.WindowUpdateSizeIncrement, preserveHighestBit: false);
                break;
        }
    }
}

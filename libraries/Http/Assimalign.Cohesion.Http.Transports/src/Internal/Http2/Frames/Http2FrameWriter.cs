using System;
using System.Buffers.Binary;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

using Assimalign.Cohesion.Http.Internal;

namespace Assimalign.Cohesion.Http.Transports.Internal.Http2;

internal static class Http2FrameWriter
{
    public static async Task WriteAsync(Stream stream, Http2Frame frame, ReadOnlyMemory<byte> payload, CancellationToken cancellationToken = default)
    {
        int extendedHeaderLength = Http2FrameReader.GetPayloadFieldsLength(frame);
        int payloadLength = checked(extendedHeaderLength + payload.Length);
        frame.PayloadLength = payloadLength;

        byte[] frameHeader = new byte[Http2FrameReader.HeaderLength + extendedHeaderLength];
        Span<byte> headerSpan = frameHeader;
        Bitshifter.WriteUInt24BigEndian(headerSpan, (uint)payloadLength);
        headerSpan[3] = (byte)frame.Type;
        headerSpan[4] = frame.Flags;
        Bitshifter.WriteUInt31BigEndian(headerSpan.Slice(5, 4), (uint)frame.StreamId, preserveHighestBit: false);

        if (extendedHeaderLength > 0)
        {
            WritePayloadFields(frame, headerSpan.Slice(Http2FrameReader.HeaderLength, extendedHeaderLength));
        }

        await stream.WriteAsync(frameHeader.AsMemory(), cancellationToken).ConfigureAwait(false);

        if (!payload.IsEmpty)
        {
            await stream.WriteAsync(payload, cancellationToken).ConfigureAwait(false);
        }
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
            case Http2FrameType.Priority:
                destination[0] = frame.PriorityIsExclusive ? (byte)0x80 : (byte)0x00;
                Bitshifter.WriteUInt31BigEndian(destination.Slice(0, 4), (uint)frame.PriorityStreamDependency);
                destination[4] = frame.PriorityWeight;
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

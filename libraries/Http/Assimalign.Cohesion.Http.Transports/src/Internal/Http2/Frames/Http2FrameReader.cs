using System;
using System.Buffers;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.IO;

using Assimalign.Cohesion.Http.Internal;

namespace Assimalign.Cohesion.Http.Transports.Internal.Http2;

internal static class Http2FrameReader
{
    public const int HeaderLength = 9;
    public const int SettingSize = 6;

    private const int TypeOffset = 3;
    private const int FlagsOffset = 4;
    private const int StreamIdOffset = 5;

    public static bool TryReadFrame(ref ReadOnlySequence<byte> buffer, Http2Frame frame, uint maxFrameSize, out ReadOnlySequence<byte> framePayload)
    {
        framePayload = ReadOnlySequence<byte>.Empty;

        if (buffer.Length < HeaderLength)
        {
            return false;
        }

        ReadOnlySpan<byte> header = AsSpan(buffer.Slice(0, HeaderLength));
        int payloadLength = (int)Bitshifter.ReadUInt24BigEndian(header);

        if (payloadLength > maxFrameSize)
        {
            // RFC 9113 §4.2 — defense in depth (the connection-context's
            // ReadFrameAsync performs the same check first).
            throw new Http2ConnectionException(
                Http2ErrorCode.FrameSizeError,
                $"HTTP/2 frame payload length {payloadLength} exceeded the negotiated maximum {maxFrameSize}.");
        }

        long frameLength = HeaderLength + payloadLength;

        if (buffer.Length < frameLength)
        {
            return false;
        }

        frame.PayloadLength = payloadLength;
        frame.Type = (Http2FrameType)header[TypeOffset];
        frame.Flags = header[FlagsOffset];
        frame.StreamId = (int)Bitshifter.ReadUInt31BigEndian(header.Slice(StreamIdOffset));

        int extendedHeaderLength = ReadExtendedFields(frame, buffer);
        framePayload = buffer.Slice(HeaderLength + extendedHeaderLength, payloadLength - extendedHeaderLength);
        buffer = buffer.Slice(framePayload.End);
        return true;
    }

    public static int GetPayloadFieldsLength(Http2Frame frame)
    {
        return frame.Type switch
        {
            Http2FrameType.Data => frame.DataHasPadding ? 1 : 0,
            Http2FrameType.Headers => (frame.HeadersHasPadding ? 1 : 0) + (frame.HeadersHasPriority ? 5 : 0),
            Http2FrameType.GoAway => 8,
            Http2FrameType.Priority => 5,
            Http2FrameType.RstStream => 4,
            Http2FrameType.WindowUpdate => 4,
            _ => 0,
        };
    }

    public static IList<Http2PeerSetting> ReadSettings(in ReadOnlySequence<byte> payload)
    {
        ReadOnlySpan<byte> data = AsSpan(payload);

        if (data.Length % SettingSize != 0)
        {
            throw new InvalidDataException("The HTTP/2 SETTINGS payload length was invalid.");
        }

        Http2PeerSetting[] settings = new Http2PeerSetting[data.Length / SettingSize];

        for (int index = 0; index < settings.Length; index++)
        {
            settings[index] = ReadSetting(data);
            data = data.Slice(SettingSize);
        }

        return settings;
    }

    private static int ReadExtendedFields(Http2Frame frame, in ReadOnlySequence<byte> readableBuffer)
    {
        int extendedHeaderLength = GetPayloadFieldsLength(frame);

        if (extendedHeaderLength > frame.PayloadLength)
        {
            // RFC 9113 §4.2 — frames that arrive with a payload too short
            // to carry their mandatory fixed fields are a connection-level
            // FRAME_SIZE_ERROR. Surface it as a typed connection error so
            // the receive loop can emit GOAWAY before tearing down.
            throw new Http2ConnectionException(
                Http2ErrorCode.FrameSizeError,
                $"HTTP/2 {frame.Type} frame payload length {frame.PayloadLength} is too short for its required fixed fields.");
        }

        ReadOnlySpan<byte> extendedHeaders = AsSpan(readableBuffer.Slice(HeaderLength, extendedHeaderLength));

        switch (frame.Type)
        {
            case Http2FrameType.Data:
                frame.DataPadLength = frame.DataHasPadding ? extendedHeaders[0] : (byte)0;
                break;
            case Http2FrameType.Headers:
                if (frame.HeadersHasPadding)
                {
                    frame.HeadersPadLength = extendedHeaders[0];
                    extendedHeaders = extendedHeaders.Slice(1);
                }

                if (frame.HeadersHasPriority)
                {
                    frame.HeadersStreamDependency = (int)Bitshifter.ReadUInt31BigEndian(extendedHeaders);
                    frame.HeadersPriorityWeight = extendedHeaders[4];
                }
                break;
            case Http2FrameType.GoAway:
                frame.GoAwayLastStreamId = (int)Bitshifter.ReadUInt31BigEndian(extendedHeaders);
                frame.GoAwayErrorCode = (Http2ErrorCode)BinaryPrimitives.ReadUInt32BigEndian(extendedHeaders.Slice(4));
                break;
            case Http2FrameType.Priority:
                frame.PriorityIsExclusive = (extendedHeaders[0] & 0x80) != 0;
                frame.PriorityStreamDependency = (int)Bitshifter.ReadUInt31BigEndian(extendedHeaders);
                frame.PriorityWeight = extendedHeaders[4];
                break;
            case Http2FrameType.RstStream:
                frame.RstStreamErrorCode = (Http2ErrorCode)BinaryPrimitives.ReadUInt32BigEndian(extendedHeaders);
                break;
            case Http2FrameType.WindowUpdate:
                frame.WindowUpdateSizeIncrement = (int)Bitshifter.ReadUInt31BigEndian(extendedHeaders);
                break;
        }

        return extendedHeaderLength;
    }

    private static Http2PeerSetting ReadSetting(ReadOnlySpan<byte> payload)
    {
        Http2SettingsParameter parameter = (Http2SettingsParameter)BinaryPrimitives.ReadUInt16BigEndian(payload);
        uint value = BinaryPrimitives.ReadUInt32BigEndian(payload.Slice(2));
        return new Http2PeerSetting(parameter, value);
    }

    private static ReadOnlySpan<byte> AsSpan(in ReadOnlySequence<byte> buffer)
    {
        return buffer.IsSingleSegment ? buffer.FirstSpan : buffer.ToArray();
    }
}

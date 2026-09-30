using System;
using System.Buffers.Binary;

using Assimalign.Cohesion.Http.Connections.Internal;

namespace Assimalign.Cohesion.Http.Connections.Tests.TestObjects;

/// <summary>
/// One HTTP/2 frame as the server wrote it: the frame-header fields (RFC 9113 §4.1) and the raw
/// payload.
/// </summary>
/// <param name="Type">The frame type octet.</param>
/// <param name="Flags">The frame flags octet.</param>
/// <param name="StreamId">The 31-bit stream identifier.</param>
/// <param name="Payload">The frame payload.</param>
internal readonly record struct Http2WireFrame(byte Type, byte Flags, int StreamId, byte[] Payload)
{
    public const byte DataType = 0x0;
    public const byte HeadersType = 0x1;
    public const byte RstStreamType = 0x3;
    public const byte PingType = 0x6;
    public const byte GoAwayType = 0x7;

    /// <summary>Whether this is a DATA frame.</summary>
    public bool IsData => Type == DataType;

    /// <summary>Whether this is a HEADERS frame.</summary>
    public bool IsHeaders => Type == HeadersType;

    /// <summary>Whether this is an RST_STREAM frame.</summary>
    public bool IsRstStream => Type == RstStreamType;

    /// <summary>Whether this is a GOAWAY frame.</summary>
    public bool IsGoAway => Type == GoAwayType;

    /// <summary>Whether this is a PING acknowledgement.</summary>
    public bool IsPingAck => Type == PingType && (Flags & 0x1) != 0;

    /// <summary>Whether a DATA or HEADERS frame carries END_STREAM (flag 0x1).</summary>
    public bool EndStream => (IsData || IsHeaders) && (Flags & 0x1) != 0;

    /// <summary>Reads the error code of an RST_STREAM frame.</summary>
    public Http2ErrorCode GetRstStreamErrorCode() => (Http2ErrorCode)BinaryPrimitives.ReadUInt32BigEndian(Payload);

    /// <summary>Reads the error code of a GOAWAY frame (the octets after the last-stream-id).</summary>
    public Http2ErrorCode GetGoAwayErrorCode() => (Http2ErrorCode)BinaryPrimitives.ReadUInt32BigEndian(Payload.AsSpan(4, 4));
}

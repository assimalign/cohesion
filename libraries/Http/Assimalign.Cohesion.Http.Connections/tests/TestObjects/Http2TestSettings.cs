using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;

using Assimalign.Cohesion.Http.Connections.Internal.Http2;

using Shouldly;

namespace Assimalign.Cohesion.Http.Connections.Tests.TestObjects;

/// <summary>
/// Low-level HTTP/2 frame and SETTINGS payload helpers for transport-level tests.
/// </summary>
/// <remarks>
/// <para>
/// Tests use these to construct intentionally malformed wire bytes (invalid
/// SETTINGS values, off-stream frames, ACK frames with payload, etc.) so the
/// server's validation paths and GOAWAY emission can be exercised without
/// depending on a real HTTP/2 client.
/// </para>
/// </remarks>
internal static class Http2TestSettings
{
    /// <summary>
    /// HTTP/2 SETTINGS parameter identifiers (mirrors
    /// <c>Http2SettingsParameter</c> in the transport so the tests can build
    /// raw payloads without depending on internals).
    /// </summary>
    public enum Parameter : ushort
    {
        HeaderTableSize = 0x1,
        EnablePush = 0x2,
        MaxConcurrentStreams = 0x3,
        InitialWindowSize = 0x4,
        MaxFrameSize = 0x5,
        MaxHeaderListSize = 0x6,
        EnableConnectProtocol = 0x8,
    }

    /// <summary>The HTTP/2 client preface (RFC 9113 §3.4).</summary>
    public static byte[] Preface()
        => Encoding.ASCII.GetBytes("PRI * HTTP/2.0\r\n\r\nSM\r\n\r\n");

    /// <summary>
    /// Builds a raw HTTP/2 frame with the supplied header fields and payload.
    /// Format: 24-bit length, 8-bit type, 8-bit flags, 31-bit stream id (high
    /// bit reserved), then the payload.
    /// </summary>
    public static byte[] RawFrame(byte frameType, byte flags, int streamId, byte[] payload)
    {
        byte[] frame = new byte[9 + payload.Length];
        frame[0] = (byte)((payload.Length >> 16) & 0xFF);
        frame[1] = (byte)((payload.Length >> 8) & 0xFF);
        frame[2] = (byte)(payload.Length & 0xFF);
        frame[3] = frameType;
        frame[4] = flags;
        frame[5] = (byte)((streamId >> 24) & 0x7F);
        frame[6] = (byte)((streamId >> 16) & 0xFF);
        frame[7] = (byte)((streamId >> 8) & 0xFF);
        frame[8] = (byte)(streamId & 0xFF);

        if (payload.Length > 0)
        {
            Buffer.BlockCopy(payload, 0, frame, 9, payload.Length);
        }

        return frame;
    }

    /// <summary>
    /// Builds a SETTINGS frame payload (6 octets per entry: 16-bit identifier,
    /// 32-bit value, big-endian).
    /// </summary>
    public static byte[] SettingsPayload(params (Parameter Parameter, uint Value)[] entries)
    {
        byte[] payload = new byte[entries.Length * 6];
        Span<byte> span = payload;

        foreach ((Parameter parameter, uint value) in entries)
        {
            BinaryPrimitives.WriteUInt16BigEndian(span, (ushort)parameter);
            BinaryPrimitives.WriteUInt32BigEndian(span.Slice(2), value);
            span = span.Slice(6);
        }

        return payload;
    }

    /// <summary>
    /// Same as <see cref="SettingsPayload"/> but accepts a raw 16-bit parameter
    /// identifier so tests can build payloads for unknown settings the
    /// strongly-typed enum does not name.
    /// </summary>
    public static byte[] SettingsPayloadRaw(params (ushort Parameter, uint Value)[] entries)
    {
        byte[] payload = new byte[entries.Length * 6];
        Span<byte> span = payload;

        foreach ((ushort parameter, uint value) in entries)
        {
            BinaryPrimitives.WriteUInt16BigEndian(span, parameter);
            BinaryPrimitives.WriteUInt32BigEndian(span.Slice(2), value);
            span = span.Slice(6);
        }

        return payload;
    }

    /// <summary>
    /// Parses a SETTINGS-frame payload into a dictionary keyed by
    /// <see cref="Parameter"/>. Unknown parameter IDs are dropped.
    /// </summary>
    public static Dictionary<Parameter, uint> ReadSettings(byte[] payload)
    {
        if (payload.Length % 6 != 0)
        {
            throw new InvalidDataException($"SETTINGS payload size {payload.Length} is not a multiple of 6.");
        }

        Dictionary<Parameter, uint> result = new();
        Span<byte> span = payload;

        while (span.Length >= 6)
        {
            ushort parameter = BinaryPrimitives.ReadUInt16BigEndian(span);
            uint value = BinaryPrimitives.ReadUInt32BigEndian(span.Slice(2));

            if (Enum.IsDefined((Parameter)parameter))
            {
                result[(Parameter)parameter] = value;
            }

            span = span.Slice(6);
        }

        return result;
    }

    /// <summary>
    /// Asserts that the supplied byte stream contains a GOAWAY frame (type 7)
    /// carrying <paramref name="expectedErrorCode"/> in its last 4 octets.
    /// </summary>
    public static void AssertContainsGoAway(byte[] output, Http2ErrorCode expectedErrorCode)
    {
        IReadOnlyList<(long FrameType, byte[] Payload)> frames = HttpProtocolPayloadFactory.ParseHttp2Frames(output);
        (long FrameType, byte[] Payload) goAway = frames.FirstOrDefault(frame => frame.FrameType == 7);

        goAway.Payload.ShouldNotBeNull();
        goAway.Payload.Length.ShouldBe(8);

        uint errorCode = BinaryPrimitives.ReadUInt32BigEndian(goAway.Payload.AsSpan(4, 4));
        errorCode.ShouldBe((uint)expectedErrorCode);
    }
}

using System;
using System.IO;

namespace Assimalign.Cohesion.Http.Connections.Internal.Http3.Frames;

/// <summary>
/// Serializer for the HTTP/3 <c>GOAWAY</c> frame (RFC 9114 §7.2.6). The frame
/// announces graceful connection shutdown on the control stream: its payload is
/// a single identifier — for a server, the stream ID of a client-initiated
/// bidirectional request stream — that marks the boundary of what the endpoint
/// will process. Requests on streams with an ID less than the announced value
/// may have been processed; requests at or above it are not (RFC 9114 §5.2).
/// </summary>
/// <remarks>
/// The payload is a QUIC variable-length integer (RFC 9000 §16 / RFC 9114 §5.2),
/// so unlike the malformed zero-length precursor this frame carries a non-zero
/// <c>Length</c>. Pure buffer arithmetic — no reflection — matching the AOT
/// posture of the surrounding transport.
/// </remarks>
internal static class Http3GoAwayFrame
{
    /// <summary>
    /// Encodes a complete <c>GOAWAY</c> frame: the frame type (0x07), the payload
    /// length, and the payload — <paramref name="streamId"/> as a QUIC
    /// variable-length integer (RFC 9114 §5.2 / §7.2.6).
    /// </summary>
    /// <param name="streamId">
    /// The client-initiated bidirectional stream ID marking the processing
    /// boundary. Must be non-negative; QUIC stream IDs are unsigned varints.
    /// </param>
    /// <returns>The serialized frame bytes, ready to write to the control stream.</returns>
    /// <exception cref="ArgumentOutOfRangeException">
    /// Thrown when <paramref name="streamId"/> is negative.
    /// </exception>
    public static byte[] Encode(long streamId)
    {
        if (streamId < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(streamId), streamId, "An HTTP/3 GOAWAY stream identifier must be non-negative.");
        }

        // Encode the varint payload first so the frame's Length prefix reflects
        // the actual encoded width (1, 2, 4, or 8 octets per RFC 9000 §16).
        using MemoryStream payload = new();
        QuicVariableLengthInteger.Write(payload, streamId);
        byte[] payloadBytes = payload.ToArray();

        using MemoryStream frame = new();
        QuicVariableLengthInteger.Write(frame, (long)Http3FrameType.GoAway);
        QuicVariableLengthInteger.Write(frame, payloadBytes.Length);
        frame.Write(payloadBytes, 0, payloadBytes.Length);

        return frame.ToArray();
    }
}

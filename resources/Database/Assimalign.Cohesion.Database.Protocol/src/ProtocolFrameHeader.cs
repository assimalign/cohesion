using System;
using System.Buffers.Binary;

namespace Assimalign.Cohesion.Database.Protocol;

/// <summary>
/// The fixed 5-byte header that precedes every protocol frame:
/// a big-endian unsigned 32-bit payload length followed by a one-byte
/// <see cref="ProtocolMessageType"/>.
/// </summary>
/// <param name="Type">The message type of the frame.</param>
/// <param name="PayloadLength">The length of the payload that follows the header, in bytes.</param>
public readonly record struct ProtocolFrameHeader(ProtocolMessageType Type, uint PayloadLength)
{
    /// <summary>
    /// The size of the encoded header in bytes.
    /// </summary>
    public const int Size = 5;

    /// <summary>
    /// The maximum payload length a peer may send, guarding allocation from
    /// untrusted length prefixes. Larger logical payloads (blob streams, large
    /// result rows) are chunked across frames.
    /// </summary>
    public const uint MaxPayloadLength = 16 * 1024 * 1024;

    /// <summary>
    /// Writes the header to the start of the destination buffer.
    /// </summary>
    /// <param name="destination">The buffer to write to; must be at least <see cref="Size"/> bytes.</param>
    /// <exception cref="ArgumentException">Thrown when the destination is smaller than <see cref="Size"/>.</exception>
    public void WriteTo(Span<byte> destination)
    {
        if (destination.Length < Size)
        {
            throw new ArgumentException($"Destination must be at least {Size} bytes.", nameof(destination));
        }
        BinaryPrimitives.WriteUInt32BigEndian(destination, PayloadLength);
        destination[4] = (byte)Type;
    }

    /// <summary>
    /// Attempts to parse a header from the start of the source buffer.
    /// </summary>
    /// <param name="source">The buffer to parse from.</param>
    /// <param name="header">When this method returns true, the parsed header.</param>
    /// <returns>
    /// True when the buffer held at least <see cref="Size"/> bytes and the length
    /// prefix was within <see cref="MaxPayloadLength"/>; otherwise false.
    /// </returns>
    public static bool TryParse(ReadOnlySpan<byte> source, out ProtocolFrameHeader header)
    {
        header = default;
        if (source.Length < Size)
        {
            return false;
        }
        var length = BinaryPrimitives.ReadUInt32BigEndian(source);
        if (length > MaxPayloadLength)
        {
            return false;
        }
        header = new ProtocolFrameHeader((ProtocolMessageType)source[4], length);
        return true;
    }
}

using System;
using System.IO;

namespace Assimalign.Cohesion.Http.Transports.Internal.Http3.QPack;

/// <summary>
/// Encodes and decodes QPACK prefixed integers (RFC 9204 §4.1.1, the same
/// representation as HPACK RFC 7541 §5.1): an N-bit prefix in the first
/// byte, optionally continued by 7-bit groups with a continuation flag.
/// </summary>
internal static class QPackPrefixedInteger
{
    /// <summary>
    /// Decodes a prefixed integer beginning at <paramref name="index"/>.
    /// The high bits of the first byte outside the <paramref name="prefixBits"/>
    /// prefix are ignored (they carry the representation pattern).
    /// </summary>
    /// <param name="buffer">The encoded field section.</param>
    /// <param name="index">
    /// On entry, the position of the prefix byte; on return, the position
    /// immediately after the integer.
    /// </param>
    /// <param name="prefixBits">The prefix width in bits (1..8).</param>
    /// <returns>The decoded value.</returns>
    /// <exception cref="InvalidDataException">
    /// Thrown when the integer is truncated or overflows.
    /// </exception>
    public static long Decode(ReadOnlySpan<byte> buffer, ref int index, int prefixBits)
    {
        if (index >= buffer.Length)
        {
            throw new InvalidDataException("The QPACK prefixed integer is truncated.");
        }

        int prefixMax = (1 << prefixBits) - 1;
        long value = buffer[index++] & prefixMax;

        if (value < prefixMax)
        {
            return value;
        }

        int shift = 0;

        while (true)
        {
            if (index >= buffer.Length)
            {
                throw new InvalidDataException("The QPACK prefixed integer is truncated.");
            }

            byte continuation = buffer[index++];
            value += (long)(continuation & 0x7F) << shift;

            // RFC 9204 §4.1.1 — bound the encoded length so a hostile peer
            // cannot drive an unbounded shift / overflow.
            if (shift > 62 || value < 0)
            {
                throw new InvalidDataException("The QPACK prefixed integer overflowed.");
            }

            if ((continuation & 0x80) == 0)
            {
                return value;
            }

            shift += 7;
        }
    }

    /// <summary>
    /// Encodes a prefixed integer, OR-ing the representation pattern in
    /// <paramref name="prefixMask"/> into the first byte.
    /// </summary>
    /// <param name="destination">The stream to write to.</param>
    /// <param name="value">The non-negative value to encode.</param>
    /// <param name="prefixBits">The prefix width in bits (1..8).</param>
    /// <param name="prefixMask">
    /// The high-bit pattern that identifies the representation; OR-ed into
    /// the first byte.
    /// </param>
    public static void Encode(Stream destination, long value, int prefixBits, byte prefixMask)
    {
        int prefixMax = (1 << prefixBits) - 1;

        if (value < prefixMax)
        {
            destination.WriteByte((byte)(prefixMask | value));
            return;
        }

        destination.WriteByte((byte)(prefixMask | prefixMax));
        value -= prefixMax;

        while (value >= 0x80)
        {
            destination.WriteByte((byte)((value & 0x7F) | 0x80));
            value >>= 7;
        }

        destination.WriteByte((byte)value);
    }
}

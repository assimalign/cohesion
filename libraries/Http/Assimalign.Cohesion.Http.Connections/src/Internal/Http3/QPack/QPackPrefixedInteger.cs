using System;
using System.IO;

namespace Assimalign.Cohesion.Http.Connections.Internal;

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
            throw new InvalidDataException(ErrorMessages.QpackPrefixedIntegerIsTruncated);
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
                throw new InvalidDataException(ErrorMessages.QpackPrefixedIntegerIsTruncated);
            }

            byte continuation = buffer[index++];
            value += (long)(continuation & 0x7F) << shift;

            // RFC 9204 §4.1.1 — bound the encoded length so a hostile peer
            // cannot drive an unbounded shift / overflow.
            if (shift > 62 || value < 0)
            {
                throw new InvalidDataException(ErrorMessages.QpackPrefixedIntegerOverflowed);
            }

            if ((continuation & 0x80) == 0)
            {
                return value;
            }

            shift += 7;
        }
    }

    /// <summary>
    /// Attempts to decode a prefixed integer beginning at <paramref name="index"/> from octets that may end
    /// inside it, as an instruction stream read incrementally does. An integer takes at most ten octets, so
    /// ten buffered octets always decide it.
    /// </summary>
    /// <param name="buffer">The buffered octets.</param>
    /// <param name="index">
    /// On entry, the position of the prefix byte; on a successful return, the position immediately after the
    /// integer; otherwise unchanged.
    /// </param>
    /// <param name="prefixBits">The prefix width in bits (1..8).</param>
    /// <param name="value">The decoded value, or zero when the integer is incomplete.</param>
    /// <returns><see langword="true"/> when the integer is complete; <see langword="false"/> when more octets are needed.</returns>
    /// <exception cref="InvalidDataException">Thrown when the integer overflows.</exception>
    public static bool TryDecode(ReadOnlySpan<byte> buffer, ref int index, int prefixBits, out long value)
    {
        int position = index;
        value = 0;

        if (position >= buffer.Length)
        {
            return false;
        }

        int prefixMax = (1 << prefixBits) - 1;
        long result = buffer[position++] & prefixMax;

        if (result < prefixMax)
        {
            index = position;
            value = result;
            return true;
        }

        int shift = 0;

        while (true)
        {
            if (position >= buffer.Length)
            {
                return false;
            }

            byte continuation = buffer[position++];
            result += (long)(continuation & 0x7F) << shift;

            if (result < 0)
            {
                throw new InvalidDataException(ErrorMessages.QpackPrefixedIntegerOverflowed);
            }

            if ((continuation & 0x80) == 0)
            {
                index = position;
                value = result;
                return true;
            }

            // RFC 9204 §4.1.1 — checked before waiting for another octet, so a continuation that can only
            // overflow is refused at once instead of buffered.
            shift += 7;

            if (shift > 62)
            {
                throw new InvalidDataException(ErrorMessages.QpackPrefixedIntegerOverflowed);
            }
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

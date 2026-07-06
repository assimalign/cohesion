using System;
using System.Text;

using Assimalign.Cohesion.Http.Connections.Internal.Http2.HPack;
using Assimalign.Cohesion.Http.Connections.Internal.Http3.Frames;

namespace Assimalign.Cohesion.Http.Connections.Internal.Http3.QPack;

/// <summary>
/// Parses the QPACK encoder-stream instructions the peer sends and applies them
/// to the decoder's <see cref="QPackDynamicTable"/> (RFC 9204 §4.3): Set Dynamic
/// Table Capacity (§4.3.1), Insert with Name Reference (§4.3.2), Insert with
/// Literal Name (§4.3.3), and Duplicate (§4.3.4).
/// </summary>
/// <remarks>
/// The parser is incremental: instructions can be split across transport reads,
/// so <see cref="TryApplyNext"/> reports how many octets it consumed and returns
/// <see langword="false"/> (consuming nothing) when the buffer holds only a
/// partial instruction. A genuinely malformed instruction — an integer overflow,
/// an invalid Huffman string, or a table violation — throws a
/// <see cref="QPackException"/> (<c>QPACK_ENCODER_STREAM_ERROR</c>), a
/// connection error.
/// </remarks>
internal static class QPackEncoderInstructionParser
{
    /// <summary>
    /// Parses and applies the next complete encoder instruction at the start of
    /// <paramref name="buffer"/>.
    /// </summary>
    /// <param name="buffer">The buffered, not-yet-consumed encoder-stream octets.</param>
    /// <param name="table">The dynamic table the instruction mutates.</param>
    /// <param name="consumed">The number of octets consumed (0 when incomplete).</param>
    /// <param name="inserted">
    /// <see langword="true"/> when the instruction added an entry to the table
    /// (Insert*/Duplicate), so the caller can advance its insert-count tracking.
    /// </param>
    /// <returns>
    /// <see langword="true"/> when a complete instruction was applied; otherwise
    /// <see langword="false"/> (more octets are required).
    /// </returns>
    /// <exception cref="QPackException">Thrown on a malformed instruction or table violation.</exception>
    public static bool TryApplyNext(ReadOnlySpan<byte> buffer, QPackDynamicTable table, out int consumed, out bool inserted)
    {
        consumed = 0;
        inserted = false;

        if (buffer.IsEmpty)
        {
            return false;
        }

        int index = 0;
        byte first = buffer[0];

        if ((first & 0x80) != 0)
        {
            // §4.3.2 Insert with Name Reference: 1 T name-index(6) then value.
            bool isStatic = (first & 0x40) != 0;

            if (!TryDecodeInteger(buffer, ref index, 6, out long nameIndex) ||
                !TryDecodeString(buffer, ref index, 7, out string value))
            {
                return false;
            }

            if (isStatic)
            {
                table.InsertWithStaticNameReference((int)nameIndex, value);
            }
            else
            {
                table.InsertWithDynamicNameReference(nameIndex, value);
            }

            inserted = true;
        }
        else if ((first & 0x40) != 0)
        {
            // §4.3.3 Insert with Literal Name: 0 1 H name-len(5) name, then value.
            if (!TryDecodeString(buffer, ref index, 5, out string name) ||
                !TryDecodeString(buffer, ref index, 7, out string value))
            {
                return false;
            }

            table.InsertWithLiteralName(name, value);
            inserted = true;
        }
        else if ((first & 0x20) != 0)
        {
            // §4.3.1 Set Dynamic Table Capacity: 0 0 1 capacity(5).
            if (!TryDecodeInteger(buffer, ref index, 5, out long capacity))
            {
                return false;
            }

            table.SetCapacity(capacity);
        }
        else
        {
            // §4.3.4 Duplicate: 0 0 0 relative-index(5).
            if (!TryDecodeInteger(buffer, ref index, 5, out long relativeIndex))
            {
                return false;
            }

            table.Duplicate(relativeIndex);
            inserted = true;
        }

        consumed = index;
        return true;
    }

    private static bool TryDecodeInteger(ReadOnlySpan<byte> buffer, ref int index, int prefixBits, out long value)
    {
        int start = index;
        value = 0;

        if (index >= buffer.Length)
        {
            return false;
        }

        int prefixMax = (1 << prefixBits) - 1;
        long result = buffer[index++] & prefixMax;

        if (result < prefixMax)
        {
            value = result;
            return true;
        }

        int shift = 0;

        while (true)
        {
            if (index >= buffer.Length)
            {
                index = start;
                return false;
            }

            byte continuation = buffer[index++];
            result += (long)(continuation & 0x7F) << shift;

            if (shift > 62 || result < 0)
            {
                throw new QPackException(
                    Http3ErrorCode.QPackEncoderStreamError,
                    "A QPACK encoder-stream integer overflowed.");
            }

            if ((continuation & 0x80) == 0)
            {
                value = result;
                return true;
            }

            shift += 7;
        }
    }

    private static bool TryDecodeString(ReadOnlySpan<byte> buffer, ref int index, int prefixBits, out string value)
    {
        int start = index;
        value = string.Empty;

        if (index >= buffer.Length)
        {
            return false;
        }

        bool huffman = (buffer[index] & (1 << prefixBits)) != 0;

        if (!TryDecodeInteger(buffer, ref index, prefixBits, out long length))
        {
            index = start;
            return false;
        }

        if (length < 0)
        {
            throw new QPackException(
                Http3ErrorCode.QPackEncoderStreamError,
                "A QPACK encoder-stream string has a negative length.");
        }

        if (index + length > buffer.Length)
        {
            // The octets have not all arrived yet — rewind and wait for more.
            index = start;
            return false;
        }

        ReadOnlySpan<byte> octets = buffer.Slice(index, (int)length);
        index += (int)length;

        if (!huffman)
        {
            value = Encoding.Latin1.GetString(octets);
            return true;
        }

        try
        {
            value = Encoding.Latin1.GetString(HPackHuffmanDecoder.Decode(octets));
            return true;
        }
        catch (HPackDecodingException ex)
        {
            throw new QPackException(
                Http3ErrorCode.QPackEncoderStreamError,
                "A QPACK encoder-stream string has an invalid Huffman encoding.",
                ex);
        }
    }
}

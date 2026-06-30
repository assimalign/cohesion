using System;
using System.IO;
using System.Text;

using Assimalign.Cohesion.Http.Connections.Internal.Http2.HPack;

namespace Assimalign.Cohesion.Http.Connections.Internal.Http3.QPack;

/// <summary>
/// Decodes and encodes QPACK string literals (RFC 9204 §4.1.2). A string
/// is a Huffman flag (the bit immediately above the length prefix), an
/// N-bit prefixed length, and that many octets. QPACK reuses the HPACK
/// Huffman code (RFC 7541 Appendix B), so decoding delegates to the
/// shared <see cref="HPackHuffmanDecoder"/>.
/// </summary>
internal static class QPackStringCodec
{
    /// <summary>
    /// Decodes a string literal beginning at <paramref name="index"/>.
    /// </summary>
    /// <param name="buffer">The encoded field section.</param>
    /// <param name="index">
    /// On entry, the position of the length-prefix byte; on return, the
    /// position immediately after the string octets.
    /// </param>
    /// <param name="prefixBits">
    /// The length prefix width; the Huffman flag is the bit at
    /// <c>1 &lt;&lt; prefixBits</c> in the first byte.
    /// </param>
    /// <returns>The decoded string.</returns>
    /// <exception cref="InvalidDataException">
    /// Thrown when the literal is truncated or its Huffman encoding is
    /// invalid.
    /// </exception>
    public static string Decode(ReadOnlySpan<byte> buffer, ref int index, int prefixBits)
    {
        if (index >= buffer.Length)
        {
            throw new InvalidDataException("The QPACK string literal is truncated.");
        }

        bool huffman = (buffer[index] & (1 << prefixBits)) != 0;
        long length = QPackPrefixedInteger.Decode(buffer, ref index, prefixBits);

        if (length < 0 || index + length > buffer.Length)
        {
            throw new InvalidDataException("The QPACK string literal length exceeds the field section.");
        }

        ReadOnlySpan<byte> octets = buffer.Slice(index, (int)length);
        index += (int)length;

        if (!huffman)
        {
            return Encoding.Latin1.GetString(octets);
        }

        try
        {
            return Encoding.Latin1.GetString(HPackHuffmanDecoder.Decode(octets));
        }
        catch (HPackDecodingException ex)
        {
            // Surface as the per-stream parse failure the HTTP/3 receive
            // loop already isolates (RFC 9204 §2.2 — a Huffman error is a
            // QPACK decompression failure).
            throw new InvalidDataException("The QPACK string literal has an invalid Huffman encoding.", ex);
        }
    }

    /// <summary>
    /// Encodes a string literal without Huffman coding (the Huffman flag is
    /// left clear). Huffman coding is optional for an encoder (RFC 9204
    /// §4.1.2), and emitting raw octets keeps the output deterministic and
    /// allocation-light.
    /// </summary>
    /// <param name="destination">The stream to write to.</param>
    /// <param name="value">The string to encode (octets are Latin-1).</param>
    /// <param name="prefixBits">The length prefix width.</param>
    /// <param name="prefixMask">
    /// The representation pattern OR-ed into the first byte; the Huffman
    /// flag bit must be clear in this mask.
    /// </param>
    public static void Encode(Stream destination, string value, int prefixBits, byte prefixMask)
    {
        byte[] octets = Encoding.Latin1.GetBytes(value);
        QPackPrefixedInteger.Encode(destination, octets.Length, prefixBits, prefixMask);
        destination.Write(octets, 0, octets.Length);
    }
}

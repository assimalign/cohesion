using System;

namespace Assimalign.Cohesion.Http.Connections.Internal.Http2.HPack;

/// <summary>
/// Encodes octet strings with the canonical HPACK/QPACK Huffman code
/// (RFC 7541 Appendix B). Codes are emitted MSB-first and the final octet is
/// right-padded with the EOS prefix (all ones) per RFC 7541 §5.2, so the
/// output round-trips through <see cref="HPackHuffmanDecoder"/>.
/// </summary>
/// <remarks>
/// <para>
/// The shared home under the HPACK folder is deliberate: HTTP/2 HPACK and
/// HTTP/3 QPACK share the same Appendix B code (RFC 9204 §4.1.2 explicitly
/// reuses the HPACK table), so a single encoder serves both string-literal
/// paths. Huffman coding is optional for an encoder, so callers use
/// <see cref="GetEncodedLength"/> to emit the Huffman form only when it is
/// strictly shorter than the raw octets.
/// </para>
/// <para>
/// AOT posture: the encoder is pure span/bit arithmetic over the constant
/// <see cref="HPackHuffmanCodes.Table"/>; no reflection or runtime code
/// generation.
/// </para>
/// </remarks>
internal static class HPackHuffmanEncoder
{
    /// <summary>
    /// Computes the number of octets the Huffman encoding of
    /// <paramref name="source"/> would occupy, without allocating. Callers use
    /// this to decide whether the Huffman form is shorter than the raw octets
    /// before committing to it.
    /// </summary>
    /// <param name="source">The octets to measure.</param>
    /// <returns>The Huffman-encoded length in octets, including EOS padding.</returns>
    public static int GetEncodedLength(ReadOnlySpan<byte> source)
    {
        long bits = 0;

        foreach (byte symbol in source)
        {
            bits += HPackHuffmanCodes.Table[symbol].BitLength;
        }

        // Round the total bit length up to a whole octet; the trailing bits
        // are the all-ones EOS padding (RFC 7541 §5.2).
        return (int)((bits + 7) / 8);
    }

    /// <summary>
    /// Encodes <paramref name="source"/> into <paramref name="destination"/>.
    /// </summary>
    /// <param name="source">The octets to encode.</param>
    /// <param name="destination">The buffer to receive the Huffman octets.</param>
    /// <returns>
    /// The number of octets written, or <c>-1</c> when
    /// <paramref name="destination"/> is too small to hold the encoding.
    /// </returns>
    public static int Encode(ReadOnlySpan<byte> source, Span<byte> destination)
    {
        // A single code is at most 30 bits and the buffer holds at most 7
        // pending bits before a symbol is added, so the working set never
        // exceeds 37 bits — comfortably inside a 64-bit accumulator.
        ulong buffer = 0;
        int bitsInBuffer = 0;
        int written = 0;

        foreach (byte symbol in source)
        {
            (uint code, byte length) = HPackHuffmanCodes.Table[symbol];
            buffer = (buffer << length) | code;
            bitsInBuffer += length;

            while (bitsInBuffer >= 8)
            {
                bitsInBuffer -= 8;

                if (written >= destination.Length)
                {
                    return -1;
                }

                destination[written++] = (byte)(buffer >> bitsInBuffer);
            }
        }

        if (bitsInBuffer > 0)
        {
            if (written >= destination.Length)
            {
                return -1;
            }

            // RFC 7541 §5.2 — pad the final octet on the right with the EOS
            // prefix, which is all ones.
            int pad = 8 - bitsInBuffer;
            destination[written++] = (byte)((buffer << pad) | ((1UL << pad) - 1));
        }

        return written;
    }

    /// <summary>
    /// Encodes <paramref name="source"/> into a freshly allocated array.
    /// </summary>
    /// <param name="source">The octets to encode.</param>
    /// <returns>The Huffman-encoded octets.</returns>
    public static byte[] Encode(ReadOnlySpan<byte> source)
    {
        byte[] result = new byte[GetEncodedLength(source)];
        Encode(source, result);
        return result;
    }
}

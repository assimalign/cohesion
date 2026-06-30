using System;

namespace Assimalign.Cohesion.Http.Connections.Internal.Http2.HPack;

/// <summary>
/// Decodes an HPACK Huffman-encoded string per RFC 7541 §5.2 + Appendix B.
/// </summary>
/// <remarks>
/// <para>
/// The decoder uses a binary tree built once from
/// <see cref="HPackHuffmanCodes.Table"/>. Each input bit walks one step
/// down the tree; reaching a leaf emits the symbol and resets to the
/// root. Trailing bits at the end of the input MUST be a strict prefix
/// of the EOS code (all 1s) and MUST NOT exceed 7 bits (RFC 7541 §5.2).
/// </para>
/// </remarks>
internal static class HPackHuffmanDecoder
{
    // Tree representation:
    //   _children[i, 0] = left child index for node i  (or symbol leaf if _isLeaf)
    //   _children[i, 1] = right child index for node i (or symbol leaf if _isLeaf)
    // Slot 0 is the root.
    private static readonly int[,] _children;
    private static readonly bool[,] _isLeaf;

    static HPackHuffmanDecoder()
    {
        // 257 leaves; canonical Huffman tree depth is 30; node count is
        // bounded by 2 * 257 ≈ 514. 1024 is comfortably above.
        const int MaxNodes = 1024;
        _children = new int[MaxNodes, 2];
        _isLeaf = new bool[MaxNodes, 2];

        int nodeCount = 1; // 0 = root.

        for (int symbol = 0; symbol < HPackHuffmanCodes.Table.Length; symbol++)
        {
            (uint code, byte length) = HPackHuffmanCodes.Table[symbol];
            int node = 0;

            for (int bit = length - 1; bit >= 0; bit--)
            {
                int direction = (int)((code >> bit) & 1);

                if (bit == 0)
                {
                    _children[node, direction] = symbol;
                    _isLeaf[node, direction] = true;
                }
                else
                {
                    if (_isLeaf[node, direction])
                    {
                        throw new InvalidOperationException(
                            "HPACK Huffman table internal consistency error: prefix-free violation.");
                    }

                    if (_children[node, direction] == 0)
                    {
                        _children[node, direction] = nodeCount++;
                    }

                    node = _children[node, direction];
                }
            }
        }
    }

    /// <summary>
    /// Decodes <paramref name="source"/> into a freshly-allocated array.
    /// </summary>
    /// <exception cref="HPackDecodingException">
    /// Thrown when the source contains an EOS symbol, padding longer
    /// than 7 bits, or padding that doesn't match the EOS prefix.
    /// </exception>
    public static byte[] Decode(ReadOnlySpan<byte> source)
    {
        byte[] scratch = new byte[source.Length * 8 / 5 + 16];
        int written = Decode(source, scratch);
        if (written == scratch.Length)
        {
            return scratch;
        }

        byte[] trimmed = new byte[written];
        Buffer.BlockCopy(scratch, 0, trimmed, 0, written);
        return trimmed;
    }

    /// <summary>
    /// Decodes <paramref name="source"/> into <paramref name="destination"/>
    /// and returns the number of bytes written.
    /// </summary>
    public static int Decode(ReadOnlySpan<byte> source, Span<byte> destination)
    {
        int node = 0;
        int written = 0;
        int bitsSinceLastSymbol = 0;

        foreach (byte b in source)
        {
            for (int bit = 7; bit >= 0; bit--)
            {
                int direction = (b >> bit) & 1;
                bitsSinceLastSymbol++;

                if (_isLeaf[node, direction])
                {
                    int symbol = _children[node, direction];
                    if (symbol == HPackHuffmanCodes.EndOfString)
                    {
                        throw new HPackDecodingException(
                            "HPACK Huffman-encoded string contained an EOS symbol.");
                    }

                    if (written >= destination.Length)
                    {
                        throw new HPackDecodingException(
                            "HPACK Huffman-encoded string longer than destination buffer.");
                    }

                    destination[written++] = (byte)symbol;
                    node = 0;
                    bitsSinceLastSymbol = 0;
                }
                else
                {
                    node = _children[node, direction];
                }
            }
        }

        // RFC 7541 §5.2 — trailing partial code MUST be ≤ 7 bits and
        // MUST be a prefix of the all-ones EOS code (i.e., every padding
        // bit must be 1).
        if (node != 0)
        {
            if (bitsSinceLastSymbol > 7)
            {
                throw new HPackDecodingException(
                    "HPACK Huffman-encoded string has trailing partial code longer than 7 bits.");
            }

            // Walk the all-ones path from `node`. Reaching a leaf means
            // a real symbol's code was a prefix of the EOS code — which
            // would be a decoding error per §5.2.
            int padNode = node;
            int remaining = bitsSinceLastSymbol;
            while (remaining > 0)
            {
                if (_isLeaf[padNode, 1])
                {
                    throw new HPackDecodingException(
                        "HPACK Huffman-encoded string padding does not match EOS prefix.");
                }

                padNode = _children[padNode, 1];
                remaining--;
            }
        }

        return written;
    }
}

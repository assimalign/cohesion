using System;
using System.Collections.Generic;

namespace Assimalign.Cohesion.Content.Pdf.Tests;

internal static partial class PdfFixtureBuilder
{
    // Independent literal-only encoder. Width follows the ordinal of the next literal, and
    // a reset starts a fresh sequence; this exercises all 9/10/11/12-bit boundaries.
    internal static byte[] LzwLiterals(int count, int earlyChange, int resets)
    {
        var bits = new List<bool>();
        void Code(int code, int width)
        {
            for (var i = width - 1; i >= 0; i--) { bits.Add((code & (1 << i)) != 0); }
        }
        var previousWidth = 9;
        for (var reset = 0; reset <= resets; reset++)
        {
            Code(256, previousWidth);
            for (var i = 0; i < count; i++)
            {
                var dictionarySize = Math.Min(4096, 258 + Math.Max(0, i - 1));
                var width = dictionarySize + earlyChange >= 2048 ? 12 : dictionarySize + earlyChange >= 1024 ? 11 : dictionarySize + earlyChange >= 512 ? 10 : 9;
                Code(i & 255, width);
            }
            var nextSize = Math.Min(4096, 258 + count - 1) + earlyChange;
            previousWidth = nextSize >= 2048 ? 12 : nextSize >= 1024 ? 11 : nextSize >= 512 ? 10 : 9;
        }
        Code(257, previousWidth);
        var bytes = new byte[(bits.Count + 7) / 8];
        for (var i = 0; i < bits.Count; i++) { if (bits[i]) { bytes[i / 8] |= (byte)(1 << (7 - i % 8)); } }
        return bytes;
    }
    internal static byte[] LzwNineBitCodes(params int[] codes)
    {
        var bytes = new byte[(codes.Length * 9 + 7) / 8];
        for (var i = 0; i < codes.Length; i++)
        {
            for (var bit = 0; bit < 9; bit++)
            {
                if ((codes[i] & (1 << (8 - bit))) != 0) { var at = i * 9 + bit; bytes[at / 8] |= (byte)(1 << (7 - at % 8)); }
            }
        }
        return bytes;
    }
}

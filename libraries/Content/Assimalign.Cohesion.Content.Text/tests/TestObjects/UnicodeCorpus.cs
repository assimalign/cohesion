using System;
using System.Collections.Generic;
using System.Text;

namespace Assimalign.Cohesion.Content.Text.Tests;

// Authored fixtures: scalars and byte sequences are constructed here from RFC 3629 sections 3/4
// and Unicode 17.0 sections 3.9/3.10. No external corpus or platform encoder supplies expectations.
internal static class UnicodeCorpus
{
    internal static Encoding EncodingFor(int codePage, bool strict = false) => codePage switch
    {
        65001 => new UTF8Encoding(false, strict),
        1200 => new UnicodeEncoding(false, false, strict),
        1201 => new UnicodeEncoding(true, false, strict),
        12000 => new UTF32Encoding(false, false, strict),
        12001 => new UTF32Encoding(true, false, strict),
        _ => throw new InvalidOperationException(),
    };

    internal static byte[] PreambleFor(int codePage) => codePage switch
    {
        65001 => [0xEF, 0xBB, 0xBF],
        1200 => [0xFF, 0xFE],
        1201 => [0xFE, 0xFF],
        12000 => [0xFF, 0xFE, 0x00, 0x00],
        12001 => [0x00, 0x00, 0xFE, 0xFF],
        _ => throw new InvalidOperationException(),
    };

    internal static IEnumerable<(string Name, string Hex, string Decoded)> InvalidUtf8()
    {
        yield return ("overlong two-byte NUL", "C080", "\uFFFD\uFFFD");
        yield return ("overlong three-byte slash", "E080AF", "\uFFFD\uFFFD\uFFFD");
        yield return ("overlong four-byte slash", "F08080AF", "\uFFFD\uFFFD\uFFFD\uFFFD");
        yield return ("encoded high surrogate", "EDA080", "\uFFFD\uFFFD\uFFFD");
        yield return ("encoded low surrogate", "EDBFBF", "\uFFFD\uFFFD\uFFFD");
        yield return ("above U+10FFFF", "F4908080", "\uFFFD\uFFFD\uFFFD\uFFFD");
        yield return ("stray continuation", "80BF", "\uFFFD\uFFFD");
        yield return ("valid ASCII after invalid prefix", "E228A1", "\uFFFD(\uFFFD");
        yield return ("truncated two-byte scalar", "C2", "\uFFFD");
        yield return ("truncated three-byte scalar at byte one", "E2", "\uFFFD");
        yield return ("truncated three-byte scalar at byte two", "E282", "\uFFFD");
        yield return ("truncated four-byte scalar at byte one", "F0", "\uFFFD");
        yield return ("truncated four-byte scalar at byte two", "F09F", "\uFFFD");
        yield return ("truncated four-byte scalar at byte three", "F09F98", "\uFFFD");
        foreach (var forbidden in new byte[] { 0xC0, 0xC1, 0xF5, 0xF6, 0xF7, 0xF8, 0xF9, 0xFA, 0xFB, 0xFC, 0xFD, 0xFE, 0xFF })
        {
            yield return ($"forbidden byte 0x{forbidden:X2}", forbidden.ToString("X2"), "\uFFFD");
        }
    }
}

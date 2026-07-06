using System;
using System.Text;

namespace Assimalign.Cohesion.Content.Text;

/// <summary>
/// Detects the Unicode encoding of text content from its leading bytes: first by byte order mark, then
/// by the null-byte patterns a Unicode stream beginning with an ASCII character produces (the scheme
/// the YAML 1.2 specification, section 5.2, standardizes), defaulting to UTF-8.
/// </summary>
/// <remarks>
/// The retained encoding scope for the Content family is Unicode: UTF-8 (the default), UTF-16 LE/BE,
/// and UTF-32 LE/BE. Legacy code pages are deliberately out of scope — callers that must consume them
/// decode externally and hand the family decoded text.
/// </remarks>
public static class TextEncodingDetector
{
    private static readonly Encoding Utf8 = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false);
    private static readonly Encoding Utf16LittleEndian = new UnicodeEncoding(bigEndian: false, byteOrderMark: false);
    private static readonly Encoding Utf16BigEndian = new UnicodeEncoding(bigEndian: true, byteOrderMark: false);
    private static readonly Encoding Utf32LittleEndian = new UTF32Encoding(bigEndian: false, byteOrderMark: false);
    private static readonly Encoding Utf32BigEndian = new UTF32Encoding(bigEndian: true, byteOrderMark: false);

    /// <summary>
    /// Detects the encoding of text content from its leading bytes. Four bytes are sufficient; shorter
    /// prefixes are detected on a best-effort basis.
    /// </summary>
    /// <param name="prefix">The leading bytes of the content.</param>
    /// <returns>The detection result, defaulting to UTF-8 when no mark or pattern matches.</returns>
    public static TextEncodingDetection Detect(ReadOnlySpan<byte> prefix)
    {
        // Byte order marks, longest first so UTF-32LE wins over the UTF-16LE prefix it contains.
        if (prefix.Length >= 4 && prefix[0] == 0x00 && prefix[1] == 0x00 && prefix[2] == 0xFE && prefix[3] == 0xFF)
        {
            return new TextEncodingDetection(Utf32BigEndian, 4, detectedFromByteOrderMark: true);
        }

        if (prefix.Length >= 4 && prefix[0] == 0xFF && prefix[1] == 0xFE && prefix[2] == 0x00 && prefix[3] == 0x00)
        {
            return new TextEncodingDetection(Utf32LittleEndian, 4, detectedFromByteOrderMark: true);
        }

        if (prefix.Length >= 3 && prefix[0] == 0xEF && prefix[1] == 0xBB && prefix[2] == 0xBF)
        {
            return new TextEncodingDetection(Utf8, 3, detectedFromByteOrderMark: true);
        }

        if (prefix.Length >= 2 && prefix[0] == 0xFE && prefix[1] == 0xFF)
        {
            return new TextEncodingDetection(Utf16BigEndian, 2, detectedFromByteOrderMark: true);
        }

        if (prefix.Length >= 2 && prefix[0] == 0xFF && prefix[1] == 0xFE)
        {
            return new TextEncodingDetection(Utf16LittleEndian, 2, detectedFromByteOrderMark: true);
        }

        // Null-byte patterns assuming the first character is ASCII (YAML 1.2 §5.2).
        if (prefix.Length >= 4 && prefix[0] == 0x00 && prefix[1] == 0x00 && prefix[2] == 0x00 && prefix[3] != 0x00)
        {
            return new TextEncodingDetection(Utf32BigEndian, 0, detectedFromByteOrderMark: false);
        }

        if (prefix.Length >= 4 && prefix[0] != 0x00 && prefix[1] == 0x00 && prefix[2] == 0x00 && prefix[3] == 0x00)
        {
            return new TextEncodingDetection(Utf32LittleEndian, 0, detectedFromByteOrderMark: false);
        }

        if (prefix.Length >= 2 && prefix[0] == 0x00 && prefix[1] != 0x00)
        {
            return new TextEncodingDetection(Utf16BigEndian, 0, detectedFromByteOrderMark: false);
        }

        if (prefix.Length >= 2 && prefix[0] != 0x00 && prefix[1] == 0x00)
        {
            return new TextEncodingDetection(Utf16LittleEndian, 0, detectedFromByteOrderMark: false);
        }

        return new TextEncodingDetection(Utf8, 0, detectedFromByteOrderMark: false);
    }
}

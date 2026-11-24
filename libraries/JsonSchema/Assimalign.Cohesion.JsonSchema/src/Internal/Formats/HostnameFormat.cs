using System;
using System.Buffers;
using System.Text;

namespace Assimalign.Cohesion.JsonSchema.Internal;

/// <summary>
/// The <c>hostname</c> format (RFC 1123 §2.1, with RFC 5891 A-labels from draft 7 on) and the
/// <c>idn-hostname</c> format (RFC 5890 §2.3.2.3 internationalized host names).
/// </summary>
/// <remarks>
/// <para>
/// A host name is dot-separated labels of letters, digits and inner hyphens, each 1 to 63 characters
/// and 253 in all. No label is empty, so a leading or trailing dot is invalid. From draft 7 on, a label
/// with hyphens in its third and fourth positions must be an A-label: <c>xn--</c> in any case and
/// Punycode that decodes to a valid NFC U-label with a non-ASCII character and encodes back to itself.
/// DNS compares ASCII without case, so the decoded ASCII letters are lowercased first. Draft 4 and
/// draft 6 apply RFC 1123 alone.
/// </para>
/// <para>
/// <c>idn-hostname</c> maps a non-ASCII value first (<see cref="Idna.Map"/>), then checks each label:
/// an ASCII label as <c>hostname</c> does, any other as a U-label whose A-label form fits in 63
/// characters, with the name's A-label form in 253. When a label holds a right-to-left character,
/// every label satisfies the RFC 5893 Bidi rule.
/// </para>
/// </remarks>
internal static class HostnameFormat
{
    private const int maxNameLength = 253;
    private const int maxLabelLength = 63;
    private const int acePrefixLength = 4;
    private const int maxPunycodeLength = maxLabelLength - acePrefixLength;

    private static readonly SearchValues<char> _letterDigitHyphen = SearchValues.Create(
        "-0123456789ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz");

    public static bool IsHostname(ReadOnlySpan<char> value, JsonSchemaVersion version) =>
        value.Length <= maxNameLength && Ascii.IsValid(value) && IsName(value, idna: version >= JsonSchemaVersion.Draft07);

    public static bool IsIdnHostname(ReadOnlySpan<char> value, JsonSchemaVersion version)
    {
        if (Ascii.IsValid(value))
        {
            return IsName(value, idna: true);
        }

        string? mapped = Idna.Map(value);
        return mapped is not null && IsName(mapped, idna: true);
    }

    private static bool IsName(ReadOnlySpan<char> name, bool idna)
    {
        if (name.IsEmpty)
        {
            return false;
        }

        Span<int> codePoints = stackalloc int[maxLabelLength];
        Span<char> punycode = stackalloc char[maxPunycodeLength];
        Span<char> text = stackalloc char[maxLabelLength * 2];
        int nameLength = -1;
        bool rightToLeft = false;
        bool bidiRule = true;
        foreach (Range range in name.Split('.'))
        {
            ReadOnlySpan<char> label = name[range];
            int labelLength = label.Length;
            if (Ascii.IsValid(label))
            {
                if (label.IsEmpty || label.Length > maxLabelLength || label[0] == '-' || label[^1] == '-' || label.ContainsAnyExcept(_letterDigitHyphen))
                {
                    return false;
                }

                if (idna && label.Length >= acePrefixLength && label[2] == '-' && label[3] == '-')
                {
                    // Only A-labels may use the reserved "??--" form (RFC 5890 §2.3.1).
                    int count = label.StartsWith("xn--", StringComparison.OrdinalIgnoreCase)
                        ? DecodeALabel(label.Slice(acePrefixLength), codePoints, punycode, text)
                        : -1;
                    if (count < 0)
                    {
                        return false;
                    }

                    rightToLeft |= Idna.IsRightToLeftLabel(codePoints.Slice(0, count));
                    bidiRule &= Idna.SatisfiesBidiRule(codePoints.Slice(0, count));
                }
                else
                {
                    // An LDH label satisfies the Bidi rule unless it starts with a digit.
                    bidiRule &= char.IsAsciiLetter(label[0]);
                }
            }
            else
            {
                int count = ReadULabel(label, codePoints);
                if (count < 0)
                {
                    return false;
                }

                int written = Punycode.Encode(codePoints.Slice(0, count), punycode);
                if (written < 0)
                {
                    return false;
                }

                labelLength = acePrefixLength + written;
                rightToLeft |= Idna.IsRightToLeftLabel(codePoints.Slice(0, count));
                bidiRule &= Idna.SatisfiesBidiRule(codePoints.Slice(0, count));
            }

            nameLength += labelLength + 1;
            if (nameLength > maxNameLength)
            {
                return false;
            }
        }

        return !rightToLeft || bidiRule;
    }

    /// <summary>
    /// Decodes an A-label's Punycode and validates the U-label (RFC 5891 §5.3 and §5.4).
    /// </summary>
    /// <returns>The number of code points written, or -1 when the label is not a valid A-label.</returns>
    private static int DecodeALabel(ReadOnlySpan<char> body, Span<int> codePoints, Span<char> punycode, Span<char> text)
    {
        int count = Punycode.Decode(body, codePoints);
        if (count <= 0)
        {
            return -1;
        }

        Span<int> label = codePoints.Slice(0, count);
        bool unicode = false;
        foreach (ref int codePoint in label)
        {
            if (codePoint is >= 'A' and <= 'Z')
            {
                codePoint += 'a' - 'A';
            }

            unicode |= codePoint >= 0x80;
        }

        // An A-label encodes a non-ASCII U-label, and canonically: the shortest encoding is the only one.
        int written = Punycode.Encode(label, punycode);
        if (!unicode || written < 0 || !body.Equals(punycode.Slice(0, written), StringComparison.OrdinalIgnoreCase) || !Idna.IsValidULabel(label))
        {
            return -1;
        }

        int length = 0;
        foreach (int codePoint in label)
        {
            length += new Rune(codePoint).EncodeToUtf16(text.Slice(length));
        }

        return text.Slice(0, length).IsNormalized(NormalizationForm.FormC) ? count : -1;
    }

    /// <summary>Reads a mapped non-ASCII label's code points and validates it as a U-label.</summary>
    /// <returns>The number of code points written, or -1 when the label is not a valid U-label.</returns>
    private static int ReadULabel(ReadOnlySpan<char> label, Span<int> codePoints)
    {
        // UTS #46 rejects an ACE prefix on a label that is not ASCII.
        if (label.StartsWith("xn--", StringComparison.OrdinalIgnoreCase))
        {
            return -1;
        }

        int count = 0;
        for (int position = 0; position < label.Length;)
        {
            // An A-label has a Punycode digit for each code point at least, so more code points cannot fit.
            if (count == maxPunycodeLength
                || Rune.DecodeFromUtf16(label.Slice(position), out Rune rune, out int consumed) != OperationStatus.Done)
            {
                return -1;
            }

            codePoints[count++] = rune.Value;
            position += consumed;
        }

        return Idna.IsValidULabel(codePoints.Slice(0, count)) ? count : -1;
    }
}

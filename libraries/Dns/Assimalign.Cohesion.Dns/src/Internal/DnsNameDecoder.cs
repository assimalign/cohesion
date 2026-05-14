using System;
using System.Text;

namespace Assimalign.Cohesion.Dns.Internal;

/// <summary>
/// Decodes a wire-format DNS name back to its dotted presentation form, handling RFC 1035
/// &#167; 4.1.4 compression pointers along the way.
/// </summary>
/// <remarks>
/// <para>
/// A wire-format name is a sequence of labels. Each label starts with a length octet:
/// </para>
/// <list type="bullet">
///   <item><description><c>00xxxxxx</c> (0&#8211;63): a literal label of <c>x</c> octets.</description></item>
///   <item><description><c>11xxxxxx xxxxxxxx</c>: a 14-bit pointer to an earlier offset in the message.</description></item>
///   <item><description><c>10</c> and <c>01</c> prefixes are reserved and reject as malformed.</description></item>
///   <item><description><c>00000000</c> (length 0): the root label terminates the name.</description></item>
/// </list>
/// <para>
/// The decoder caps pointer follow-ups to defend against compression loops, caps the total
/// reassembled name at RFC 1035's 255-octet limit, and rejects forward pointers (every
/// compression pointer MUST refer to an earlier offset).
/// </para>
/// </remarks>
internal static class DnsNameDecoder
{
    /// <summary>Maximum number of compression pointers any one name may follow. Defensive
    /// upper bound that comfortably exceeds anything seen in practice.</summary>
    private const int MaxPointerHops = 32;

    /// <summary>
    /// Reads a name from <paramref name="reader"/>, following compression pointers into
    /// <paramref name="message"/>. The cursor on <paramref name="reader"/> advances to the
    /// octet immediately after the name's last byte (or the pointer that terminated it).
    /// </summary>
    public static DnsName Read(ref DnsWireReader reader, ReadOnlySpan<byte> message)
    {
        StringBuilder builder = new();
        int hops = 0;
        int totalLength = 0;

        // We need to track whether we've followed a pointer. Once we follow one, advances
        // on the SECONDARY reader don't move the primary cursor — but the primary cursor must
        // sit immediately past the two-octet pointer that started the indirect chain.
        DnsWireReader cursor = reader;
        int? returnTo = null;

        while (true)
        {
            byte length = cursor.ReadUInt8();

            if (length == 0)
            {
                // Root label terminator.
                if (returnTo is null)
                {
                    reader.Seek(cursor.Position);
                }
                else
                {
                    reader.Seek(returnTo.Value);
                }

                if (builder.Length == 0)
                {
                    return DnsName.Root;
                }
                return new DnsName(builder.ToString());
            }

            int kind = length & 0xC0;
            if (kind == 0x00)
            {
                // Literal label.
                if (length > 63)
                {
                    DnsException.ThrowMalformed($"label length {length} exceeds the RFC 1035 limit of 63 octets");
                }

                ReadOnlySpan<byte> labelBytes = cursor.ReadBytes(length);
                if (builder.Length > 0)
                {
                    builder.Append('.');
                    totalLength++;
                }
                AppendLabel(builder, labelBytes);
                totalLength += labelBytes.Length;
                if (totalLength > 253)
                {
                    DnsException.ThrowMalformed(
                        $"reassembled name exceeds the RFC 1035 limit of 253 presentation characters");
                }
            }
            else if (kind == 0xC0)
            {
                // Compression pointer. 14-bit offset into the message.
                byte low = cursor.ReadUInt8();
                int pointer = ((length & 0x3F) << 8) | low;

                if (returnTo is null)
                {
                    // First pointer encountered — the primary reader stops here.
                    returnTo = cursor.Position;
                }

                if (pointer >= reader.Length)
                {
                    DnsException.ThrowMalformed($"compression pointer {pointer} falls outside the message (length {reader.Length})");
                }

                // RFC 1035 doesn't strictly require backward pointers, but every compliant
                // encoder produces them and accepting forward pointers would expose us to
                // straightforward DoS via loops. Reject them.
                int sourceOffset = returnTo.Value - 2;
                if (pointer >= sourceOffset)
                {
                    DnsException.ThrowMalformed(
                        $"compression pointer at offset {sourceOffset} targets non-prior offset {pointer}");
                }

                hops++;
                if (hops > MaxPointerHops)
                {
                    DnsException.ThrowMalformed($"compression pointer chain exceeds {MaxPointerHops} hops");
                }

                cursor = new DnsWireReader(message, pointer);
            }
            else
            {
                DnsException.ThrowMalformed(
                    $"label length octet 0x{length:X2} uses reserved bit pattern (top two bits 01 or 10)");
            }
        }
    }

    /// <summary>
    /// Appends a single label's bytes to <paramref name="builder"/>, escaping non-ASCII or
    /// special characters per RFC 1035 §5.1 presentation-form rules.
    /// </summary>
    private static void AppendLabel(StringBuilder builder, ReadOnlySpan<byte> labelBytes)
    {
        foreach (byte b in labelBytes)
        {
            // ASCII letters / digits / hyphen / underscore pass through verbatim (the common
            // case). Other ASCII and any 8-bit byte get backslash-escaped.
            if ((b >= 'a' && b <= 'z') ||
                (b >= 'A' && b <= 'Z') ||
                (b >= '0' && b <= '9') ||
                b == '-' || b == '_')
            {
                builder.Append((char)b);
            }
            else if (b == '.' || b == '\\')
            {
                builder.Append('\\').Append((char)b);
            }
            else if (b is >= 0x21 and <= 0x7E)
            {
                builder.Append((char)b);
            }
            else
            {
                // Non-printable / 8-bit byte → \DDD numeric escape.
                builder.Append('\\');
                builder.Append((char)('0' + (b / 100)));
                builder.Append((char)('0' + ((b / 10) % 10)));
                builder.Append((char)('0' + (b % 10)));
            }
        }
    }
}

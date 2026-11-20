using System;
using System.Buffers;
using System.Text;

namespace Assimalign.Cohesion.JsonSchema.Internal;

/// <summary>
/// The <c>email</c> format (the RFC 5321 §4.1.2 <c>Mailbox</c> rule) and the <c>idn-email</c> format
/// (the same rule as RFC 6531 §3.3 extends it).
/// </summary>
/// <remarks>
/// <para>
/// A local part is a dot-string of <c>atext</c> atoms or a quoted string of <c>qtextSMTP</c> and
/// <c>quoted-pairSMTP</c>. A domain is dot-separated <c>sub-domain</c> labels of letters, digits and
/// inner hyphens, or an address literal: a dotted quad of <c>Snum</c> values or <c>IPv6:</c> and an
/// IPv6 address. RFC 5321 reserves the general address literal for tags registered with IANA, and none
/// besides <c>IPv6</c> is, so any other bracketed content is invalid. Comments, folding whitespace and
/// display names belong to RFC 5322 headers, not to a mailbox. The grammar sets no length limits.
/// </para>
/// <para>
/// <c>idn-email</c> also allows any non-ASCII scalar value in atoms and quoted strings
/// (<c>UTF8-non-ascii</c>), and a non-ASCII domain is validated as an <c>idn-hostname</c> (RFC 6531
/// <c>sub-domain =/ U-label</c>).
/// </para>
/// </remarks>
internal static class EmailFormat
{
    private static readonly SearchValues<char> _atext = SearchValues.Create(
        "ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz0123456789!#$%&'*+-/=?^_`{|}~");

    public static bool IsEmail(ReadOnlySpan<char> value, JsonSchemaVersion version) => IsMailbox(value, international: false);

    public static bool IsIdnEmail(ReadOnlySpan<char> value, JsonSchemaVersion version) => IsMailbox(value, international: true);

    private static bool IsMailbox(ReadOnlySpan<char> value, bool international)
    {
        int at = value.IsEmpty ? -1 : value[0] == '"' ? ReadQuotedString(value, international) : ReadDotString(value, international);
        if (at < 0 || at >= value.Length || value[at] != '@')
        {
            return false;
        }

        ReadOnlySpan<char> domain = value.Slice(at + 1);
        if (domain.IsEmpty)
        {
            return false;
        }

        if (domain[0] == '[')
        {
            return IsAddressLiteral(domain);
        }

        if (international && !Ascii.IsValid(domain))
        {
            return HostnameFormat.IsIdnHostname(domain, JsonSchemaVersion.Draft202012);
        }

        return IsDomain(domain);
    }

    /// <summary>Reads <c>Dot-string = Atom *("." Atom)</c> and returns the position after it, or -1.</summary>
    private static int ReadDotString(ReadOnlySpan<char> value, bool international)
    {
        int position = 0;
        bool atomStart = true;
        while (position < value.Length && value[position] != '@')
        {
            char c = value[position];
            if (c == '.')
            {
                if (atomStart)
                {
                    return -1;
                }

                atomStart = true;
                position++;
            }
            else if (_atext.Contains(c))
            {
                atomStart = false;
                position++;
            }
            else if (international && c >= 0x80 && TryReadNonAscii(value, ref position))
            {
                atomStart = false;
            }
            else
            {
                return -1;
            }
        }

        return atomStart ? -1 : position;
    }

    /// <summary>Reads <c>Quoted-string = DQUOTE *QcontentSMTP DQUOTE</c> and returns the position after it, or -1.</summary>
    private static int ReadQuotedString(ReadOnlySpan<char> value, bool international)
    {
        int position = 1;
        while (position < value.Length)
        {
            char c = value[position];
            if (c == '"')
            {
                return position + 1;
            }

            if (c == '\\')
            {
                // quoted-pairSMTP = %d92 %d32-126
                if (position + 1 >= value.Length || value[position + 1] is < ' ' or > '~')
                {
                    return -1;
                }

                position += 2;
            }
            else if (c is >= ' ' and <= '~')
            {
                // qtextSMTP = %d32-33 / %d35-91 / %d93-126: everything printable but '"' and '\'.
                position++;
            }
            else if (!(international && c >= 0x80 && TryReadNonAscii(value, ref position)))
            {
                return -1;
            }
        }

        return -1;
    }

    /// <summary>Reads one non-ASCII scalar value (<c>UTF8-non-ascii</c>); a lone surrogate is invalid.</summary>
    private static bool TryReadNonAscii(ReadOnlySpan<char> value, ref int position)
    {
        if (Rune.DecodeFromUtf16(value.Slice(position), out _, out int consumed) != OperationStatus.Done)
        {
            return false;
        }

        position += consumed;
        return true;
    }

    /// <summary><c>Domain = sub-domain *("." sub-domain)</c>, <c>sub-domain = Let-dig [Ldh-str]</c>.</summary>
    private static bool IsDomain(ReadOnlySpan<char> domain)
    {
        foreach (Range range in domain.Split('.'))
        {
            ReadOnlySpan<char> label = domain[range];
            if (label.IsEmpty || !char.IsAsciiLetterOrDigit(label[0]) || !char.IsAsciiLetterOrDigit(label[^1]))
            {
                return false;
            }

            foreach (char c in label)
            {
                if (!char.IsAsciiLetterOrDigit(c) && c != '-')
                {
                    return false;
                }
            }
        }

        return true;
    }

    /// <summary><c>address-literal = "[" ( IPv4-address-literal / IPv6-address-literal ) "]"</c>.</summary>
    private static bool IsAddressLiteral(ReadOnlySpan<char> domain)
    {
        if (domain.Length < 2 || domain[^1] != ']')
        {
            return false;
        }

        ReadOnlySpan<char> literal = domain.Slice(1, domain.Length - 2);
        return literal.StartsWith("IPv6:", StringComparison.OrdinalIgnoreCase)
            ? IPAddressFormat.IsIPv6Address(literal.Slice(5), smtp: true)
            : IPAddressFormat.IsSmtpIPv4Address(literal);
    }
}

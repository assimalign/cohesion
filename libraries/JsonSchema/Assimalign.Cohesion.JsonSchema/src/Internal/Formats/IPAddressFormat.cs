using System;

namespace Assimalign.Cohesion.JsonSchema.Internal;

/// <summary>
/// The <c>ipv4</c> and <c>ipv6</c> formats, and the address grammars URIs and e-mail address
/// literals embed.
/// </summary>
/// <remarks>
/// An IPv4 address is a dotted quad of decimal octets without leading zeros (RFC 3986
/// <c>dec-octet</c>), which also rules out the octal reading RFC 2673's <c>1*3DIGIT</c> leaves open.
/// An IPv6 address follows RFC 4291 §2.2 as RFC 3986 <c>IPv6address</c> spells it, with an embedded
/// IPv4 address in the last 32 bits. Neither allows zone identifiers, prefix lengths, brackets or
/// surrounding whitespace.
/// </remarks>
internal static class IPAddressFormat
{
    public static bool IsIPv4(ReadOnlySpan<char> value, JsonSchemaVersion version) => IsIPv4Address(value);

    public static bool IsIPv6(ReadOnlySpan<char> value, JsonSchemaVersion version) => IsIPv6Address(value, smtp: false);

    /// <summary>RFC 3986 <c>IPv4address</c>: four <c>dec-octet</c> values.</summary>
    public static bool IsIPv4Address(ReadOnlySpan<char> value) => IsDottedQuad(value, leadingZeros: false);

    /// <summary>
    /// RFC 5321 <c>IPv4-address-literal</c>: four <c>Snum</c> values, decimal 0 to 255 in one to
    /// three digits, leading zeros allowed.
    /// </summary>
    public static bool IsSmtpIPv4Address(ReadOnlySpan<char> value) => IsDottedQuad(value, leadingZeros: true);

    /// <summary>
    /// An IPv6 address: RFC 3986 <c>IPv6address</c>, or with <paramref name="smtp"/> RFC 5321
    /// <c>IPv6-addr</c>, where <c>::</c> stands for at least two groups and the IPv4 part is an
    /// <c>IPv4-address-literal</c>.
    /// </summary>
    public static bool IsIPv6Address(ReadOnlySpan<char> value, bool smtp)
    {
        int groups = 0;
        bool compressed = false;
        int position = 0;
        if (value.Length >= 2 && value[0] == ':' && value[1] == ':')
        {
            compressed = true;
            position = 2;
        }
        else if (value.IsEmpty || value[0] == ':')
        {
            return false;
        }

        while (position < value.Length)
        {
            int start = position;
            while (position < value.Length && char.IsAsciiHexDigit(value[position]))
            {
                position++;
            }

            if (position < value.Length && value[position] == '.')
            {
                // ls32 as an IPv4 address: the rest of the address, counted as two groups.
                ReadOnlySpan<char> address = value.Slice(start);
                if (!(smtp ? IsSmtpIPv4Address(address) : IsIPv4Address(address)))
                {
                    return false;
                }

                groups += 2;
                break;
            }

            int length = position - start;
            if (length is 0 or > 4)
            {
                return false;
            }

            groups++;
            if (position == value.Length)
            {
                break;
            }

            if (value[position] != ':')
            {
                return false;
            }

            position++;
            if (position < value.Length && value[position] == ':')
            {
                if (compressed)
                {
                    return false;
                }

                compressed = true;
                position++;
            }
            else if (position == value.Length)
            {
                return false;
            }
        }

        return compressed ? groups <= (smtp ? 6 : 7) : groups == 8;
    }

    private static bool IsDottedQuad(ReadOnlySpan<char> value, bool leadingZeros)
    {
        int octets = 0;
        int position = 0;
        while (true)
        {
            int start = position;
            int number = 0;
            while (position < value.Length && position - start < 3 && char.IsAsciiDigit(value[position]))
            {
                number = (number * 10) + (value[position] - '0');
                position++;
            }

            int length = position - start;
            if (length == 0 || number > 255 || (!leadingZeros && length > 1 && value[start] == '0'))
            {
                return false;
            }

            octets++;
            if (position == value.Length)
            {
                return octets == 4;
            }

            if (octets == 4 || value[position] != '.')
            {
                return false;
            }

            position++;
        }
    }
}

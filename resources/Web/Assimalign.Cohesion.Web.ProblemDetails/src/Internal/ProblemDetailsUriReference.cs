using System;
using System.Net;
using System.Net.Sockets;

namespace Assimalign.Cohesion.Web.Internal;

/// <summary>
/// Checks the RFC 3986 URI-reference grammar without resolving or normalizing the reference.
/// Scheme-specific host and port restrictions do not belong to this generic syntax check.
/// </summary>
internal static class ProblemDetailsUriReference
{
    internal static bool IsValid(ReadOnlySpan<char> value)
    {
        int fragment = value.IndexOf('#');
        if (fragment >= 0)
        {
            if (!IsComponent(value[(fragment + 1)..], allowPath: true, allowQuery: true))
            {
                return false;
            }

            value = value[..fragment];
        }

        int query = value.IndexOf('?');
        if (query >= 0)
        {
            if (!IsComponent(value[(query + 1)..], allowPath: true, allowQuery: true))
            {
                return false;
            }

            value = value[..query];
        }

        int colon = value.IndexOf(':');
        int slash = value.IndexOf('/');
        if (colon >= 0 && (slash < 0 || colon < slash))
        {
            // A relative path's first segment cannot contain ':'. A colon here must
            // introduce an absolute URI with a valid scheme (RFC 3986 sections 3.1 and 4.2).
            if (colon == 0 || !char.IsAsciiLetter(value[0]))
            {
                return false;
            }

            for (int index = 1; index < colon; index++)
            {
                if (!char.IsAsciiLetterOrDigit(value[index]) && value[index] is not ('+' or '-' or '.'))
                {
                    return false;
                }
            }

            value = value[(colon + 1)..];
        }

        if (value.StartsWith("//", StringComparison.Ordinal))
        {
            value = value[2..];
            slash = value.IndexOf('/');
            ReadOnlySpan<char> authority = slash < 0 ? value : value[..slash];
            if (!IsAuthority(authority))
            {
                return false;
            }

            value = slash < 0 ? ReadOnlySpan<char>.Empty : value[slash..];
        }

        return IsComponent(value, allowPath: true);
    }

    private static bool IsAuthority(ReadOnlySpan<char> value)
    {
        int userInfo = value.IndexOf('@');
        if (userInfo >= 0)
        {
            if (!IsComponent(value[..userInfo], allowColon: true))
            {
                return false;
            }

            value = value[(userInfo + 1)..];
        }

        if (value.StartsWith("[", StringComparison.Ordinal))
        {
            int end = value.IndexOf(']');
            if (end < 0 || !IsIpLiteral(value[1..end]))
            {
                return false;
            }

            value = value[(end + 1)..];
            return value.IsEmpty || (value[0] == ':' && IsPort(value[1..]));
        }

        int colon = value.IndexOf(':');
        return colon < 0
            ? IsComponent(value)
            : IsComponent(value[..colon]) && IsPort(value[(colon + 1)..]);
    }

    private static bool IsIpLiteral(ReadOnlySpan<char> value)
    {
        if (value.IsEmpty)
        {
            return false;
        }

        if (value[0] is 'v' or 'V')
        {
            int dot = value.IndexOf('.');
            if (dot < 2 || dot == value.Length - 1)
            {
                return false;
            }

            for (int index = 1; index < dot; index++)
            {
                if (!char.IsAsciiHexDigit(value[index]))
                {
                    return false;
                }
            }

            ReadOnlySpan<char> address = value[(dot + 1)..];
            return !address.Contains('%') && IsComponent(address, allowColon: true);
        }

        // Numeric parsing only: no DNS or network operation. RFC 3986 IPv6address
        // excludes scope identifiers, which IPAddress would otherwise accept.
        foreach (char character in value)
        {
            if (!char.IsAsciiHexDigit(character) && character is not (':' or '.'))
            {
                return false;
            }
        }

        if (value.Contains('.') && !IsIpv4Tail(value[(value.LastIndexOf(':') + 1)..]))
        {
            return false;
        }

        return IPAddress.TryParse(value, out IPAddress? addressValue) &&
            addressValue.AddressFamily == AddressFamily.InterNetworkV6;
    }

    private static bool IsIpv4Tail(ReadOnlySpan<char> value)
    {
        int count = 0;
        foreach (Range range in value.Split('.'))
        {
            ReadOnlySpan<char> octet = value[range];
            if (octet.IsEmpty || octet.Length > 3 || (octet.Length > 1 && octet[0] == '0'))
            {
                return false;
            }

            int number = 0;
            foreach (char character in octet)
            {
                if (!char.IsAsciiDigit(character))
                {
                    return false;
                }

                number = number * 10 + character - '0';
            }

            if (number > 255)
            {
                return false;
            }

            count++;
        }

        return count == 4;
    }

    private static bool IsPort(ReadOnlySpan<char> value)
    {
        foreach (char character in value)
        {
            if (!char.IsAsciiDigit(character))
            {
                return false;
            }
        }

        return true;
    }

    private static bool IsComponent(ReadOnlySpan<char> value, bool allowColon = false, bool allowPath = false, bool allowQuery = false)
    {
        for (int index = 0; index < value.Length; index++)
        {
            char character = value[index];
            // unreserved / sub-delims, with each enclosing production's additions.
            if (char.IsAsciiLetterOrDigit(character) || character is '-' or '.' or '_' or '~'
                or '!' or '$' or '&' or '\'' or '(' or ')' or '*' or '+' or ',' or ';' or '='
                || (allowColon && character == ':')
                || (allowPath && character is ':' or '@' or '/')
                || (allowQuery && character == '?'))
            {
                continue;
            }

            if (character != '%' || index + 2 >= value.Length
                || !char.IsAsciiHexDigit(value[index + 1]) || !char.IsAsciiHexDigit(value[index + 2]))
            {
                return false;
            }

            index += 2;
        }

        return true;
    }
}

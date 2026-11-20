using System;
using System.Buffers;
using System.Text;

namespace Assimalign.Cohesion.JsonSchema.Internal;

/// <summary>
/// The <c>uri</c> and <c>uri-reference</c> formats (RFC 3986 <c>URI</c> and <c>URI-reference</c>),
/// and the <c>iri</c> and <c>iri-reference</c> formats (RFC 3987 <c>IRI</c> and <c>IRI-reference</c>).
/// </summary>
/// <remarks>
/// <para>
/// The parser follows the grammar's own decomposition: fragment, query, scheme, then either
/// <c>"//" authority path-abempty</c> or a path. Every character must belong to its component's set,
/// and <c>%</c> must start a two-digit hexadecimal escape. A host is an IP literal (an IPv6 address or
/// <c>IPvFuture</c> in brackets) or a <c>reg-name</c>, which also covers every dotted quad, so
/// <c>http://999.999.999.999/</c> is a valid URI as the grammar says. A port is digits only. In a
/// relative reference the first path segment may not contain <c>:</c>.
/// </para>
/// <para>
/// IRIs also allow <c>ucschar</c> wherever RFC 3986 allows unreserved characters, and
/// <c>iprivate</c> in the query. Schemes, ports and IP literals stay ASCII.
/// </para>
/// </remarks>
internal static class UriFormat
{
    private static readonly SearchValues<char> _unreservedAndSubDelims = SearchValues.Create(
        "ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz0123456789-._~!$&'()*+,;=");

    public static bool IsUri(ReadOnlySpan<char> value, JsonSchemaVersion version) => IsValid(value, iri: false, reference: false);

    public static bool IsUriReference(ReadOnlySpan<char> value, JsonSchemaVersion version) => IsValid(value, iri: false, reference: true);

    public static bool IsIri(ReadOnlySpan<char> value, JsonSchemaVersion version) => IsValid(value, iri: true, reference: false);

    public static bool IsIriReference(ReadOnlySpan<char> value, JsonSchemaVersion version) => IsValid(value, iri: true, reference: true);

    /// <summary>RFC 3987 <c>ucschar</c>.</summary>
    public static bool IsUcsChar(int codePoint)
    {
        if (codePoint < 0x10000)
        {
            return codePoint is (>= 0xA0 and <= 0xD7FF) or (>= 0xF900 and <= 0xFDCF) or (>= 0xFDF0 and <= 0xFFEF);
        }

        // Planes 1 to 13 and the upper part of plane 14, without each plane's last two code points.
        return (codePoint & 0xFFFF) <= 0xFFFD && (codePoint < 0xE0000 || (codePoint >= 0xE1000 && codePoint < 0xF0000));
    }

    /// <summary>RFC 3987 <c>iprivate</c>.</summary>
    public static bool IsIPrivate(int codePoint) =>
        codePoint is (>= 0xE000 and <= 0xF8FF) || (codePoint >= 0xF0000 && (codePoint & 0xFFFF) <= 0xFFFD);

    private static bool IsValid(ReadOnlySpan<char> value, bool iri, bool reference)
    {
        int hash = value.IndexOf('#');
        if (hash >= 0)
        {
            if (!IsComponent(value.Slice(hash + 1), Component.QueryOrFragment, iri, privateUse: false))
            {
                return false;
            }

            value = value.Slice(0, hash);
        }

        int question = value.IndexOf('?');
        if (question >= 0)
        {
            if (!IsComponent(value.Slice(question + 1), Component.QueryOrFragment, iri, privateUse: iri))
            {
                return false;
            }

            value = value.Slice(0, question);
        }

        int schemeLength = GetSchemeLength(value);
        bool hasScheme = schemeLength > 0;
        if (hasScheme)
        {
            value = value.Slice(schemeLength + 1);
        }
        else if (!reference)
        {
            return false;
        }

        if (value.StartsWith("//", StringComparison.Ordinal))
        {
            ReadOnlySpan<char> rest = value.Slice(2);
            int slash = rest.IndexOf('/');
            ReadOnlySpan<char> authority = slash >= 0 ? rest.Slice(0, slash) : rest;
            return IsAuthority(authority, iri) && (slash < 0 || IsComponent(rest.Slice(slash), Component.Path, iri, privateUse: false));
        }

        if (!hasScheme && !value.IsEmpty && value[0] != '/')
        {
            // path-noscheme: the first segment has no ':', which would read as a scheme.
            int slash = value.IndexOf('/');
            if ((slash >= 0 ? value.Slice(0, slash) : value).Contains(':'))
            {
                return false;
            }
        }

        return IsComponent(value, Component.Path, iri, privateUse: false);
    }

    /// <summary>Returns the length of the scheme before a <c>:</c>, or zero when the value has none.</summary>
    private static int GetSchemeLength(ReadOnlySpan<char> value)
    {
        if (value.IsEmpty || !char.IsAsciiLetter(value[0]))
        {
            return 0;
        }

        for (int i = 1; i < value.Length; i++)
        {
            char c = value[i];
            if (c == ':')
            {
                return i;
            }

            if (!char.IsAsciiLetterOrDigit(c) && c is not ('+' or '-' or '.'))
            {
                return 0;
            }
        }

        return 0;
    }

    private static bool IsAuthority(ReadOnlySpan<char> authority, bool iri)
    {
        int at = authority.IndexOf('@');
        if (at >= 0)
        {
            if (!IsComponent(authority.Slice(0, at), Component.UserInfo, iri, privateUse: false))
            {
                return false;
            }

            authority = authority.Slice(at + 1);
        }

        ReadOnlySpan<char> port;
        if (!authority.IsEmpty && authority[0] == '[')
        {
            int close = authority.IndexOf(']');
            if (close < 0 || !IsIPLiteral(authority.Slice(1, close - 1)))
            {
                return false;
            }

            ReadOnlySpan<char> after = authority.Slice(close + 1);
            if (!after.IsEmpty && after[0] != ':')
            {
                return false;
            }

            port = after.IsEmpty ? after : after.Slice(1);
        }
        else
        {
            int colon = authority.IndexOf(':');
            ReadOnlySpan<char> host = colon >= 0 ? authority.Slice(0, colon) : authority;
            if (!IsComponent(host, Component.RegName, iri, privateUse: false))
            {
                return false;
            }

            port = colon >= 0 ? authority.Slice(colon + 1) : default;
        }

        foreach (char c in port)
        {
            if (!char.IsAsciiDigit(c))
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>The inside of RFC 3986 <c>IP-literal</c>: <c>IPv6address / IPvFuture</c>.</summary>
    private static bool IsIPLiteral(ReadOnlySpan<char> value)
    {
        if (value.IsEmpty || value[0] is not ('v' or 'V'))
        {
            return IPAddressFormat.IsIPv6Address(value, smtp: false);
        }

        // IPvFuture = "v" 1*HEXDIG "." 1*( unreserved / sub-delims / ":" )
        int position = 1;
        while (position < value.Length && char.IsAsciiHexDigit(value[position]))
        {
            position++;
        }

        if (position == 1 || position >= value.Length - 1 || value[position] != '.')
        {
            return false;
        }

        foreach (char c in value.Slice(position + 1))
        {
            if (c != ':' && !_unreservedAndSubDelims.Contains(c))
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>
    /// Determines whether every character of a component belongs to its set, reading <c>%</c>
    /// escapes and, for IRIs, <c>ucschar</c> (and <c>iprivate</c> where allowed).
    /// </summary>
    private static bool IsComponent(ReadOnlySpan<char> value, Component component, bool iri, bool privateUse)
    {
        int position = 0;
        while (position < value.Length)
        {
            char c = value[position];
            if (c == '%')
            {
                if (position + 2 >= value.Length || !char.IsAsciiHexDigit(value[position + 1]) || !char.IsAsciiHexDigit(value[position + 2]))
                {
                    return false;
                }

                position += 3;
                continue;
            }

            if (c < 0x80)
            {
                if (!_unreservedAndSubDelims.Contains(c) && !IsComponentDelimiter(c, component))
                {
                    return false;
                }

                position++;
                continue;
            }

            if (!iri
                || Rune.DecodeFromUtf16(value.Slice(position), out Rune rune, out int consumed) != OperationStatus.Done
                || !(IsUcsChar(rune.Value) || (privateUse && IsIPrivate(rune.Value))))
            {
                return false;
            }

            position += consumed;
        }

        return true;
    }

    /// <summary>Returns whether a delimiter beyond the unreserved characters and sub-delims belongs to a component.</summary>
    private static bool IsComponentDelimiter(char c, Component component) => component switch
    {
        // segment = *pchar; pchar adds ":" and "@"; paths keep their "/" separators.
        Component.Path => c is ':' or '@' or '/',
        Component.QueryOrFragment => c is ':' or '@' or '/' or '?',
        Component.UserInfo => c == ':',
        _ => false,
    };

    private enum Component
    {
        Path,
        QueryOrFragment,
        UserInfo,
        RegName,
    }
}

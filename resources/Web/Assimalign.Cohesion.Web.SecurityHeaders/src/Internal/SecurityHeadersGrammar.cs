using System;

namespace Assimalign.Cohesion.Web.SecurityHeaders.Internal;

/// <summary>
/// The grammar checks every builder runs before a value can reach a response header. Each predicate is
/// the production of the specification named on it; none of them accepts a control character, a line
/// break, or a character outside printable ASCII, so no configured value can inject a second field or
/// split a policy.
/// </summary>
internal static class SecurityHeadersGrammar
{
    /// <summary>
    /// CSP3 <c>directive-name = 1*( ALPHA / DIGIT / "-" )</c>.
    /// </summary>
    public static bool IsDirectiveName(ReadOnlySpan<char> value)
    {
        if (value.IsEmpty)
        {
            return false;
        }

        foreach (char c in value)
        {
            if (!IsAlpha(c) && !IsDigit(c) && c != '-')
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>
    /// CSP3 <c>directive-value = *( required-ascii-whitespace / ( %x21-%x2B / %x2D-%x3A / %x3C-%x7E ) )</c>,
    /// narrowed to the space character for whitespace: a header value never carries a tab or a line break.
    /// The exclusions are <c>,</c> (it separates policies) and <c>;</c> (it separates directives).
    /// </summary>
    public static bool IsDirectiveValue(ReadOnlySpan<char> value)
    {
        foreach (char c in value)
        {
            if (c != ' ' && (c < '!' || c > '~' || c == ',' || c == ';'))
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>
    /// RFC 3986 <c>scheme = ALPHA *( ALPHA / DIGIT / "+" / "-" / "." )</c>.
    /// </summary>
    public static bool IsScheme(ReadOnlySpan<char> value)
    {
        if (value.IsEmpty || !IsAlpha(value[0]))
        {
            return false;
        }

        foreach (char c in value[1..])
        {
            if (!IsAlpha(c) && !IsDigit(c) && c != '+' && c != '-' && c != '.')
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>
    /// CSP3 <c>host-source = [ scheme-part "://" ] host-part [ ":" port-part ] [ path-part ]</c>, where
    /// <c>host-part = "*" / [ "*." ] 1*host-char *( "." 1*host-char ) [ "." ]</c>,
    /// <c>port-part = 1*DIGIT / "*"</c>, and the path is an RFC 3986 absolute path whose <c>,</c> and
    /// <c>;</c> are percent-encoded (CSP3 §2.3.1).
    /// </summary>
    /// <remarks>
    /// A bare keyword name (<c>self</c>, <c>none</c>, <c>unsafe-inline</c>, …) parses as a host of that
    /// name, which is never what was meant: it is a keyword missing its quotes, so it is refused.
    /// </remarks>
    public static bool IsHostSource(ReadOnlySpan<char> value)
    {
        if (value.IsEmpty || IsUnquotedKeyword(value))
        {
            return false;
        }

        ReadOnlySpan<char> rest = value;

        int schemeEnd = rest.IndexOf(':');
        if (schemeEnd > 0 && rest[schemeEnd..].StartsWith("://", StringComparison.Ordinal) && IsScheme(rest[..schemeEnd]))
        {
            rest = rest[(schemeEnd + 3)..];
        }

        int hostEnd = rest.IndexOfAny(':', '/');
        ReadOnlySpan<char> host = hostEnd < 0 ? rest : rest[..hostEnd];
        if (!IsHostPart(host))
        {
            return false;
        }

        rest = hostEnd < 0 ? ReadOnlySpan<char>.Empty : rest[hostEnd..];

        if (!rest.IsEmpty && rest[0] == ':')
        {
            int portEnd = rest.IndexOf('/');
            ReadOnlySpan<char> port = portEnd < 0 ? rest[1..] : rest[1..portEnd];
            if (!port.SequenceEqual("*") && !IsDigits(port))
            {
                return false;
            }

            rest = portEnd < 0 ? ReadOnlySpan<char>.Empty : rest[portEnd..];
        }

        return rest.IsEmpty || IsAbsolutePath(rest);
    }

    /// <summary>
    /// CSP3 <c>ancestor-source = scheme-source / host-source / "'self'"</c>, the only sources the
    /// <c>frame-ancestors</c> directive accepts.
    /// </summary>
    public static bool IsAncestorSource(ReadOnlySpan<char> value)
    {
        return value.Equals("'self'", StringComparison.OrdinalIgnoreCase)
            || IsSchemeSource(value)
            || IsHostSource(value);
    }

    /// <summary>
    /// CSP3 <c>scheme-source = scheme-part ":"</c>.
    /// </summary>
    public static bool IsSchemeSource(ReadOnlySpan<char> value)
    {
        return value.Length > 1 && value[^1] == ':' && IsScheme(value[..^1]);
    }

    /// <summary>
    /// CSP3 <c>base64-value = 1*( ALPHA / DIGIT / "+" / "/" / "-" / "_" ) *2( "=" )</c>, the grammar of a
    /// nonce and of a hash digest.
    /// </summary>
    public static bool IsBase64Value(ReadOnlySpan<char> value)
    {
        int index = 0;
        while (index < value.Length && IsBase64Character(value[index]))
        {
            index++;
        }

        if (index == 0 || value.Length - index > 2)
        {
            return false;
        }

        for (; index < value.Length; index++)
        {
            if (value[index] != '=')
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>
    /// RFC 9110 <c>token = 1*tchar</c>.
    /// </summary>
    public static bool IsToken(ReadOnlySpan<char> value)
    {
        if (value.IsEmpty)
        {
            return false;
        }

        foreach (char c in value)
        {
            if (!IsTokenCharacter(c))
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>
    /// A <c>report-uri</c> entry: a URI reference written in printable ASCII with no space, <c>,</c>, or
    /// <c>;</c>, which would end the entry, the directive, or the policy.
    /// </summary>
    public static bool IsReportUri(string value)
    {
        if (value.Length == 0)
        {
            return false;
        }

        foreach (char c in value)
        {
            if (c <= ' ' || c > '~' || c == ',' || c == ';')
            {
                return false;
            }
        }

        return Uri.TryCreate(value, UriKind.RelativeOrAbsolute, out _);
    }

    /// <summary>
    /// The RFC 9651 <c>key</c> grammar a Permissions Policy feature identifier uses:
    /// <c>( lcalpha / "*" ) *( lcalpha / DIGIT / "_" / "-" / "." / "*" )</c>.
    /// </summary>
    public static bool IsFeatureName(ReadOnlySpan<char> value)
    {
        if (value.IsEmpty || !(IsLowerAlpha(value[0]) || value[0] == '*'))
        {
            return false;
        }

        foreach (char c in value[1..])
        {
            if (!IsLowerAlpha(c) && !IsDigit(c) && c != '_' && c != '-' && c != '.' && c != '*')
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>
    /// A serialized origin for a Permissions Policy allowlist: <c>scheme "://" host [ ":" port ]</c>,
    /// with an optional <c>*.</c> subdomain wildcard on the host, and no user information, path, query,
    /// or fragment.
    /// </summary>
    public static bool IsOrigin(ReadOnlySpan<char> value)
    {
        int schemeEnd = value.IndexOf(':');
        if (schemeEnd <= 0 || !value[schemeEnd..].StartsWith("://", StringComparison.Ordinal) || !IsScheme(value[..schemeEnd]))
        {
            return false;
        }

        ReadOnlySpan<char> rest = value[(schemeEnd + 3)..];
        int portStart = rest.IndexOf(':');
        ReadOnlySpan<char> host = portStart < 0 ? rest : rest[..portStart];
        if (host.SequenceEqual("*") || !IsHostPart(host))
        {
            return false;
        }

        return portStart < 0 || IsDigits(rest[(portStart + 1)..]);
    }

    /// <summary>
    /// Whether a serialized policy list (CSP3 <c>serialized-policy-list</c>: policies separated by
    /// <c>,</c>, directives by <c>;</c>) contains a directive named <paramref name="directiveName"/>.
    /// Directive names compare ASCII case-insensitively.
    /// </summary>
    public static bool DeclaresDirective(ReadOnlySpan<char> policies, ReadOnlySpan<char> directiveName)
    {
        while (!policies.IsEmpty)
        {
            int end = policies.IndexOfAny(',', ';');
            ReadOnlySpan<char> directive = (end < 0 ? policies : policies[..end]).TrimStart(" \t\r\n\f");

            int nameEnd = directive.IndexOfAny(" \t\r\n\f");
            ReadOnlySpan<char> name = nameEnd < 0 ? directive : directive[..nameEnd];
            if (name.Equals(directiveName, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }

            policies = end < 0 ? ReadOnlySpan<char>.Empty : policies[(end + 1)..];
        }

        return false;
    }

    private static bool IsUnquotedKeyword(ReadOnlySpan<char> value)
    {
        foreach (string keyword in (ReadOnlySpan<string>)["self", "none", "unsafe-inline", "unsafe-eval", "unsafe-hashes",
            "strict-dynamic", "report-sample", "wasm-unsafe-eval"])
        {
            if (value.Equals(keyword, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }

    private static bool IsHostPart(ReadOnlySpan<char> host)
    {
        if (host.SequenceEqual("*"))
        {
            return true;
        }

        if (host.StartsWith("*.", StringComparison.Ordinal))
        {
            host = host[2..];
        }

        if (!host.IsEmpty && host[^1] == '.')
        {
            host = host[..^1];
        }

        if (host.IsEmpty)
        {
            return false;
        }

        int labelLength = 0;
        foreach (char c in host)
        {
            if (c == '.')
            {
                if (labelLength == 0)
                {
                    return false;
                }

                labelLength = 0;
                continue;
            }

            if (!IsAlpha(c) && !IsDigit(c) && c != '-')
            {
                return false;
            }

            labelLength++;
        }

        return labelLength > 0;
    }

    private static bool IsAbsolutePath(ReadOnlySpan<char> path)
    {
        if (path.IsEmpty || path[0] != '/')
        {
            return false;
        }

        for (int index = 0; index < path.Length; index++)
        {
            char c = path[index];
            if (c == '%')
            {
                if (index + 2 >= path.Length || !char.IsAsciiHexDigit(path[index + 1]) || !char.IsAsciiHexDigit(path[index + 2]))
                {
                    return false;
                }

                index += 2;
                continue;
            }

            // RFC 3986 pchar and "/", less the "," and ";" CSP requires percent-encoded.
            bool allowed = IsAlpha(c) || IsDigit(c) || c is '-' or '.' or '_' or '~' or '!' or '$' or '&'
                or '\'' or '(' or ')' or '*' or '+' or '=' or ':' or '@' or '/';
            if (!allowed)
            {
                return false;
            }
        }

        return true;
    }

    private static bool IsDigits(ReadOnlySpan<char> value)
    {
        if (value.IsEmpty)
        {
            return false;
        }

        foreach (char c in value)
        {
            if (!IsDigit(c))
            {
                return false;
            }
        }

        return true;
    }

    private static bool IsTokenCharacter(char c)
    {
        return IsAlpha(c) || IsDigit(c) || c is '!' or '#' or '$' or '%' or '&' or '\'' or '*' or '+' or '-'
            or '.' or '^' or '_' or '`' or '|' or '~';
    }

    private static bool IsBase64Character(char c) => IsAlpha(c) || IsDigit(c) || c is '+' or '/' or '-' or '_';

    private static bool IsAlpha(char c) => char.IsAsciiLetter(c);

    private static bool IsLowerAlpha(char c) => char.IsAsciiLetterLower(c);

    private static bool IsDigit(char c) => char.IsAsciiDigit(c);
}

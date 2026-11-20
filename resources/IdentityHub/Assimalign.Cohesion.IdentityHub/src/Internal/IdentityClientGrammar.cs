using System;
using System.Net;

namespace Assimalign.Cohesion.IdentityHub.Internal;

/// <summary>
/// The grammar rules of the app registration vocabulary. Each rule lives here once, so a value
/// object, a factory, and the codec that reads a stored registration all refuse the same input.
/// </summary>
internal static class IdentityClientGrammar
{
    internal const int MaxNameLength = 128;
    internal const int MaxCredentialIdLength = 64;
    internal const int MaxPermissionNameLength = 128;
    internal const int MaxAudienceLength = 256;
    internal const int MaxRedirectLength = 512;
    internal const int MaxIssuerLength = 512;
    internal const int MaxSubjectLength = 512;
    internal const int MaxDisplayNameLength = 128;
    internal const int MaxDescriptionLength = 256;
    internal const int MaxSecretLength = 512;

    /// <summary>
    /// An identifier is the unreserved characters of RFC 3986 section 2.3 (letters, digits,
    /// '-', '.', '_', '~'), starting and ending with a letter or digit. It needs no escaping in
    /// a URL path segment, a form body, or the user name of HTTP Basic authentication, and it is
    /// never "." or "..".
    /// </summary>
    internal static bool IsIdentifier(string? value, int maxLength)
    {
        if (value is null || value.Length is 0 || value.Length > maxLength)
        {
            return false;
        }

        if (!IsLetterOrDigit(value[0]) || !IsLetterOrDigit(value[^1]))
        {
            return false;
        }

        for (int index = 1; index < value.Length - 1; index++)
        {
            char character = value[index];
            if (!IsLetterOrDigit(character) && character is not ('-' or '.' or '_' or '~'))
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>
    /// A permission name is a scope token of RFC 6749 section 3.3: visible ASCII without the
    /// space, the double quote, and the backslash. It is one entry of a space-delimited
    /// <c>scope</c> parameter and one value of a <c>scp</c> or <c>roles</c> claim.
    /// </summary>
    internal static bool IsPermissionName(string? value)
    {
        if (value is null || value.Length is 0 or > MaxPermissionNameLength)
        {
            return false;
        }

        for (int index = 0; index < value.Length; index++)
        {
            char character = value[index];
            if (character is < '!' or > '~' or '"' or '\\')
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>
    /// An audience is the StringOrURI of RFC 7519 section 2, narrowed to visible ASCII: any
    /// such string, and when it holds a ':' it starts with a URI scheme and carries no fragment.
    /// </summary>
    /// <remarks>
    /// The rule is checked by hand, not with <see cref="Uri"/>, whose reading of a string that
    /// starts with '/' or a drive letter depends on the operating system.
    /// </remarks>
    internal static bool IsAudience(string? value)
    {
        if (!IsVisibleAscii(value, MaxAudienceLength))
        {
            return false;
        }

        int colon = value!.IndexOf(':');
        if (colon < 0)
        {
            return true;
        }

        return IsScheme(value.AsSpan(0, colon)) && !value.Contains('#');
    }

    /// <summary>
    /// Text a person reads: no control character and well-formed UTF-16, not blank. A
    /// description may also hold line breaks and tabs.
    /// </summary>
    internal static bool IsText(string? value, int maxLength, bool allowLineBreaks)
    {
        if (value is null || value.Length is 0 || value.Length > maxLength)
        {
            return false;
        }

        bool visible = false;
        for (int index = 0; index < value.Length; index++)
        {
            char character = value[index];
            if (char.IsControl(character))
            {
                if (!allowLineBreaks || character is not ('\r' or '\n' or '\t'))
                {
                    return false;
                }

                continue;
            }

            if (char.IsHighSurrogate(character))
            {
                if (index + 1 >= value.Length || !char.IsLowSurrogate(value[index + 1]))
                {
                    return false;
                }

                index++;
                visible = true;
                continue;
            }

            if (char.IsLowSurrogate(character))
            {
                return false;
            }

            visible |= !char.IsWhiteSpace(character);
        }

        return visible;
    }

    /// <summary>
    /// A client secret is the VSCHAR string of RFC 6749 appendix A.2: printable ASCII, the
    /// space included, and not blank. Every such secret has one byte form, so the form sent in
    /// a request body and the form sent in HTTP Basic authentication verify alike.
    /// </summary>
    internal static bool IsSecret(string? value)
    {
        if (value is null || value.Length is 0 or > MaxSecretLength)
        {
            return false;
        }

        bool visible = false;
        for (int index = 0; index < value.Length; index++)
        {
            char character = value[index];
            if (character is < ' ' or > '~')
            {
                return false;
            }

            visible |= character is not ' ';
        }

        return visible;
    }

    /// <summary>
    /// An issuer is the issuer identifier of OpenID Connect Discovery section 3: an https URL
    /// with a host and no user information, query, or fragment.
    /// </summary>
    internal static bool IsIssuer(string? value)
    {
        if (!IsVisibleAscii(value, MaxIssuerLength) || value!.Contains('#') || value.Contains('?'))
        {
            return false;
        }

        return TryParse(value, out Uri? uri)
            && string.Equals(uri.Scheme, Uri.UriSchemeHttps, StringComparison.Ordinal)
            && uri.Host.Length > 0
            && uri.UserInfo.Length is 0;
    }

    /// <summary>
    /// Checks a redirect URI against the rules of its platform.
    /// </summary>
    /// <returns>The reason the URI is refused, or <see langword="null"/> when it is accepted.</returns>
    internal static string? CheckRedirect(string? value, IdentityClientPlatform platform)
    {
        if (!IsUriText(value, MaxRedirectLength))
        {
            return ErrorMessages.RedirectUriInvalid;
        }

        // A redirect URI is matched as an exact string, so a '*' would be a literal character
        // that a reader takes for a wildcard.
        if (value!.Contains('*'))
        {
            return ErrorMessages.RedirectUriWildcard;
        }

        if (value.Contains('#'))
        {
            return ErrorMessages.RedirectUriFragment;
        }

        if (!TryParse(value, out Uri? uri) || uri.UserInfo.Length is not 0)
        {
            return ErrorMessages.RedirectUriInvalid;
        }

        bool https = string.Equals(uri.Scheme, Uri.UriSchemeHttps, StringComparison.Ordinal);
        if (https || string.Equals(uri.Scheme, Uri.UriSchemeHttp, StringComparison.Ordinal))
        {
            if (!TryReadHost(value, out ReadOnlySpan<char> host))
            {
                return ErrorMessages.RedirectUriInvalid;
            }

            return https || IsLoopback(host, uri.HostNameType) ? null : ErrorMessages.RedirectUriInsecure;
        }

        // RFC 8252 section 7.1: a private-use scheme is a domain name the application
        // controls, in reverse order. Requiring the '.' also refuses javascript:, data:, and
        // file:, none of which holds one.
        if (platform is IdentityClientPlatform.Native && uri.Scheme.Contains('.'))
        {
            return null;
        }

        return ErrorMessages.RedirectUriSchemeRefused;
    }

    /// <summary>
    /// Reads the host of an http or https URI from the string as it was written.
    /// </summary>
    /// <remarks>
    /// The parser's own reading is not used. It reports the host name "loopback" as
    /// "localhost", which only some resolvers map to the local machine, and it accepts an
    /// authority that is not introduced by "//" or that holds an empty user information part.
    /// The string is what a browser is sent, so the string is what is judged.
    /// </remarks>
    private static bool TryReadHost(string value, out ReadOnlySpan<char> host)
    {
        host = default;

        ReadOnlySpan<char> rest = value.AsSpan(value.IndexOf(':') + 1);
        if (!rest.StartsWith("//", StringComparison.Ordinal))
        {
            return false;
        }

        rest = rest[2..];
        int end = rest.IndexOfAny('/', '?');
        ReadOnlySpan<char> authority = end < 0 ? rest : rest[..end];
        if (authority.Contains('@'))
        {
            return false;
        }

        if (authority.StartsWith("[", StringComparison.Ordinal))
        {
            int close = authority.IndexOf(']');
            if (close < 0)
            {
                return false;
            }

            host = authority[1..close];
        }
        else
        {
            int port = authority.LastIndexOf(':');
            host = port < 0 ? authority : authority[..port];
        }

        return !host.IsEmpty;
    }

    private static bool IsLoopback(ReadOnlySpan<char> host, UriHostNameType hostNameType)
    {
        return hostNameType switch
        {
            UriHostNameType.Dns => host.Equals("localhost", StringComparison.OrdinalIgnoreCase),
            UriHostNameType.IPv4 or UriHostNameType.IPv6 =>
                IPAddress.TryParse(host, out IPAddress? address) && IPAddress.IsLoopback(address),
            _ => false,
        };
    }

    private static bool TryParse(string value, out Uri uri)
    {
        uri = null!;
        int colon = value.IndexOf(':');
        if (colon < 1 || !IsScheme(value.AsSpan(0, colon)))
        {
            return false;
        }

        // The scheme the parser reports must be the one the string spells. That refuses a
        // string the parser reads as an implicit file path, such as a drive letter.
        return Uri.TryCreate(value, UriKind.Absolute, out uri!)
            && value.AsSpan(0, colon).Equals(uri.Scheme, StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsScheme(ReadOnlySpan<char> value)
    {
        // RFC 3986 section 3.1.
        if (value.IsEmpty || !char.IsAsciiLetter(value[0]))
        {
            return false;
        }

        for (int index = 1; index < value.Length; index++)
        {
            char character = value[index];
            if (!IsLetterOrDigit(character) && character is not ('+' or '-' or '.'))
            {
                return false;
            }
        }

        return true;
    }

    private static bool IsVisibleAscii(string? value, int maxLength)
    {
        if (value is null || value.Length is 0 || value.Length > maxLength)
        {
            return false;
        }

        for (int index = 0; index < value.Length; index++)
        {
            if (value[index] is < '!' or > '~')
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>
    /// Visible ASCII without the characters RFC 3986 gives no place in a URI: the double quote,
    /// the angle brackets, the backslash, the caret, the backtick, the braces, and the bar.
    /// </summary>
    /// <remarks>
    /// A browser reads a backslash in an http URL as a slash and other parsers do not, so one
    /// string would name two hosts. Refusing what is not a URI character leaves one reading.
    /// </remarks>
    private static bool IsUriText(string? value, int maxLength)
    {
        if (!IsVisibleAscii(value, maxLength))
        {
            return false;
        }

        return value.AsSpan().IndexOfAny("\"<>\\^`{|}") < 0;
    }

    private static bool IsLetterOrDigit(char character) => char.IsAsciiLetterOrDigit(character);
}

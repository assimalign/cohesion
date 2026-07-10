using System;
using System.Buffers;

namespace Assimalign.Cohesion.Http;

/// <summary>
/// RFC 6265 &#167; 4.1.1 cookie grammar primitives: <c>cookie-name</c> (an
/// RFC 9110 <c>token</c>) and <c>cookie-value</c> (<c>*cookie-octet</c>,
/// optionally wrapped in a single pair of DQUOTEs) classification.
/// </summary>
/// <remarks>
/// <para>
/// Shared by <see cref="HttpCookie"/> construction &#8212; which rejects an
/// out-of-grammar name or value so a hostile octet (<c>;</c>, <c>,</c>,
/// whitespace, a control character, or CR/LF) cannot split or corrupt the
/// emitted <c>Set-Cookie</c> line &#8212; and by
/// <see cref="HttpCookieCollection"/> parsing, which silently drops
/// out-of-grammar cookies for wire robustness (RFC 6265bis parsing guidance)
/// instead of throwing on hostile inbound data.
/// </para>
/// <para>
/// Both classifiers are pure span scans over <see cref="SearchValues{T}"/>
/// membership sets &#8212; no allocation, no regex, no reflection &#8212; so
/// the package stays trim- and NativeAOT-safe.
/// </para>
/// </remarks>
internal static class HttpCookieGrammar
{
    // RFC 9110 §5.6.2 tchar — the character set of an RFC 6265 §4.1.1
    // cookie-name token. Excludes controls, whitespace, and separators
    // (notably '=', ';', and ',').
    private static readonly SearchValues<char> TokenChars = SearchValues.Create(
        "!#$%&'*+-.^_`|~0123456789ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz");

    // RFC 6265 §4.1.1 cookie-octet = %x21 / %x23-2B / %x2D-3A / %x3C-5B / %x5D-7E:
    // US-ASCII minus CTLs (incl. CR/LF), whitespace (SP), DQUOTE, comma,
    // semicolon, and backslash — precisely the octets that cannot appear in a
    // bare cookie value without breaking the Cookie / Set-Cookie wire grammar.
    private static readonly SearchValues<char> CookieOctets = SearchValues.Create(BuildCookieOctets());

    /// <summary>
    /// Determines whether <paramref name="name"/> is a non-empty RFC 6265
    /// &#167; 4.1.1 <c>cookie-name</c> (an RFC 9110 <c>token</c>).
    /// </summary>
    /// <param name="name">The candidate cookie name.</param>
    /// <returns><see langword="true"/> when every character is a token character and the span is non-empty.</returns>
    public static bool IsValidName(ReadOnlySpan<char> name)
        => !name.IsEmpty && !name.ContainsAnyExcept(TokenChars);

    /// <summary>
    /// Determines whether <paramref name="value"/> is a well-formed RFC 6265
    /// &#167; 4.1.1 <c>cookie-value</c> (<c>*cookie-octet</c>, optionally
    /// wrapped in a single pair of DQUOTEs). The empty string is well-formed.
    /// </summary>
    /// <param name="value">The candidate cookie value.</param>
    /// <returns><see langword="true"/> when the value contains only cookie-octets (ignoring an optional surrounding DQUOTE pair).</returns>
    public static bool IsValidValue(ReadOnlySpan<char> value)
    {
        // cookie-value = *cookie-octet — the empty string is well-formed.
        if (value.IsEmpty)
        {
            return true;
        }

        // A cookie-value may be wrapped in a single pair of DQUOTEs; the quotes
        // are part of the stored value, but the octets between them still may
        // not include a bare DQUOTE (RFC 6265 §4.1.1).
        if (value.Length >= 2 && value[0] == '"' && value[^1] == '"')
        {
            value = value[1..^1];
        }

        return !value.ContainsAnyExcept(CookieOctets);
    }

    private static char[] BuildCookieOctets()
    {
        // 1 + 9 + 14 + 32 + 34 = 90 permitted octets across the five ranges.
        char[] octets = new char[90];
        int i = 0;
        octets[i++] = (char)0x21;
        for (char c = (char)0x23; c <= 0x2B; c++)
        {
            octets[i++] = c;
        }
        for (char c = (char)0x2D; c <= 0x3A; c++)
        {
            octets[i++] = c;
        }
        for (char c = (char)0x3C; c <= 0x5B; c++)
        {
            octets[i++] = c;
        }
        for (char c = (char)0x5D; c <= 0x7E; c++)
        {
            octets[i++] = c;
        }
        return octets;
    }
}

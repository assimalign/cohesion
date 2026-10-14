using System;
using System.Buffers;

namespace Assimalign.Cohesion.Http.Internal;

/// <summary>
/// RFC 6265 &#167; 4.1.1 cookie grammar primitives: <c>cookie-name</c> (an
/// RFC 9110 <c>token</c>), <c>cookie-value</c> (<c>*cookie-octet</c>,
/// optionally wrapped in a single pair of DQUOTEs), and the <c>path-value</c>
/// and <c>domain-value</c> of the <c>Path</c> and <c>Domain</c> attributes.
/// </summary>
/// <remarks>
/// <para>
/// Shared by <see cref="HttpCookie"/> construction and the
/// <see cref="HttpCookieOptions"/> <c>Path</c> and <c>Domain</c> setters
/// &#8212; which reject an out-of-grammar name, value, or attribute so a
/// hostile octet (<c>;</c>, <c>,</c>, whitespace, a control character, or
/// CR/LF) cannot split or corrupt the emitted <c>Set-Cookie</c> line or add
/// attributes to it &#8212; and by <see cref="HttpCookieCollection"/>
/// parsing, which silently drops out-of-grammar cookies for wire robustness
/// (RFC 6265bis parsing guidance) instead of throwing on hostile inbound data.
/// </para>
/// <para>
/// Every classifier is a pure span scan, over <see cref="SearchValues{T}"/>
/// membership sets or a character range &#8212; no allocation, no regex, no
/// reflection &#8212; so the package stays trim- and NativeAOT-safe.
/// </para>
/// </remarks>
internal static class HttpCookieGrammar
{
    // RFC 1034 §3.1: a domain name is at most 255 octets on the wire, which is
    // 253 characters of dotted text without the root's trailing dot.
    private const int MaxDomainLength = 253;

    // RFC 1034 §3.5: a label is at most 63 characters.
    private const int MaxLabelLength = 63;

    // RFC 1034 §3.5 as enhanced by RFC 1123 §2.1: a label is letters, digits,
    // and hyphens (LDH).
    private static readonly SearchValues<char> _labelChars = SearchValues.Create(
        "-0123456789ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz");

    // RFC 9110 §5.6.2 tchar — the character set of an RFC 6265 §4.1.1
    // cookie-name token. Excludes controls, whitespace, and separators
    // (notably '=', ';', and ',').
    private static readonly SearchValues<char> _tokenChars = SearchValues.Create(
        "!#$%&'*+-.^_`|~0123456789ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz");

    // RFC 6265 §4.1.1 cookie-octet = %x21 / %x23-2B / %x2D-3A / %x3C-5B / %x5D-7E:
    // US-ASCII minus CTLs (incl. CR/LF), whitespace (SP), DQUOTE, comma,
    // semicolon, and backslash — precisely the octets that cannot appear in a
    // bare cookie value without breaking the Cookie / Set-Cookie wire grammar.
    private static readonly SearchValues<char> _cookieOctets = SearchValues.Create(BuildCookieOctets());

    /// <summary>
    /// Determines whether <paramref name="name"/> is a non-empty RFC 6265
    /// &#167; 4.1.1 <c>cookie-name</c> (an RFC 9110 <c>token</c>).
    /// </summary>
    /// <param name="name">The candidate cookie name.</param>
    /// <returns><see langword="true"/> when every character is a token character and the span is non-empty.</returns>
    public static bool IsValidName(ReadOnlySpan<char> name)
        => !name.IsEmpty && !name.ContainsAnyExcept(_tokenChars);

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

        return !value.ContainsAnyExcept(_cookieOctets);
    }

    /// <summary>
    /// Determines whether <paramref name="path"/> is an RFC 6265 &#167; 4.1.1
    /// <c>path-value</c>: any US-ASCII character except a control character
    /// (<c>0x00</c>&#8211;<c>0x1F</c>, <c>0x7F</c>) or <c>;</c>. The empty
    /// string is well-formed.
    /// </summary>
    /// <param name="path">The candidate <c>Path</c> attribute value.</param>
    /// <returns><see langword="true"/> when every character lies in <c>0x20</c>&#8211;<c>0x7E</c> and none is <c>;</c>.</returns>
    /// <remarks>
    /// RFC 6265bis spells the same set out as <c>av-octet = %x20-3A / %x3C-7E</c>.
    /// A <c>;</c> would end the attribute and start one of the value's own
    /// (<c>Path=/x; SameSite=None</c>), and a control character would corrupt
    /// the line, so neither may reach <see cref="HttpCookie.ToString"/>. A
    /// character outside US-ASCII is not a <c>CHAR</c>; percent-encode it.
    /// </remarks>
    public static bool IsValidPath(ReadOnlySpan<char> path)
        => !path.ContainsAnyExceptInRange(' ', '~') && !path.Contains(';');

    /// <summary>
    /// Determines whether <paramref name="domain"/> is an RFC 6265 &#167; 4.1.1
    /// <c>domain-value</c>: an RFC 1034 &#167; 3.5 <c>subdomain</c> as
    /// enhanced by RFC 1123 &#167; 2.1, optionally preceded by one dot.
    /// </summary>
    /// <param name="domain">The candidate <c>Domain</c> attribute value.</param>
    /// <returns>
    /// <see langword="true"/> when, after one optional leading dot, the value
    /// is one or more dot-separated labels of at most 63 letters, digits, and
    /// hyphens that neither start nor end with a hyphen, and is at most 253
    /// characters long after that optional leading dot.
    /// </returns>
    /// <remarks>
    /// The leading dot is tolerated because a user agent ignores it (RFC 6265
    /// &#167; 5.2.3). Anything else outside the grammar &#8212; an empty label
    /// (<c>a..b</c>, a trailing dot), <c>;</c>, whitespace, a control
    /// character, an underscore, or a character outside US-ASCII &#8212; is
    /// refused. An internationalized name is written in its A-label
    /// (<c>xn--</c>) form.
    /// </remarks>
    public static bool IsValidDomain(ReadOnlySpan<char> domain)
    {
        if (!domain.IsEmpty && domain[0] == '.')
        {
            domain = domain[1..];
        }

        if (domain.IsEmpty || domain.Length > MaxDomainLength)
        {
            return false;
        }

        while (true)
        {
            int dot = domain.IndexOf('.');
            ReadOnlySpan<char> label = dot < 0 ? domain : domain[..dot];

            if (!IsValidLabel(label))
            {
                return false;
            }

            if (dot < 0)
            {
                return true;
            }

            domain = domain[(dot + 1)..];
        }
    }

    private static bool IsValidLabel(ReadOnlySpan<char> label)
        => label.Length is > 0 and <= MaxLabelLength
            && label[0] != '-'
            && label[^1] != '-'
            && !label.ContainsAnyExcept(_labelChars);

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

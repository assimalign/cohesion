using System;
using System.Buffers;

namespace Assimalign.Cohesion.Http.Internal;

/// <summary>
/// Low-level RFC 9110 &#167; 5.6 field-value syntax primitives — token classification,
/// optional-whitespace (OWS) trimming, and quote-aware delimiter scanning — shared by
/// the media-type and Accept-family parsers so they split, trim, and validate field
/// values identically instead of each re-deriving the grammar.
/// </summary>
internal static class HttpFieldSyntax
{
    /// <summary>
    /// The RFC 9110 &#167; 5.6.2 <c>tchar</c> set: the characters permitted in a bare
    /// <c>token</c> (and therefore in a media type/subtype, parameter name, and unquoted
    /// parameter value).
    /// </summary>
    public static readonly SearchValues<char> TokenChars = SearchValues.Create(
        "!#$%&'*+-.^_`|~0123456789ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz");

    /// <summary>
    /// Determines whether <paramref name="value"/> is a non-empty RFC 9110 &#167; 5.6.2
    /// <c>token</c> (every character is a <c>tchar</c>).
    /// </summary>
    /// <param name="value">The candidate token.</param>
    /// <returns><see langword="true"/> when the span is a valid, non-empty token.</returns>
    public static bool IsToken(ReadOnlySpan<char> value)
        => !value.IsEmpty && !value.ContainsAnyExcept(TokenChars);

    /// <summary>
    /// Trims leading and trailing optional whitespace (OWS = SP / HTAB, RFC 9110
    /// &#167; 5.6.3) from <paramref name="value"/>.
    /// </summary>
    /// <param name="value">The span to trim.</param>
    /// <returns>The span with surrounding SP/HTAB removed.</returns>
    public static ReadOnlySpan<char> TrimOws(ReadOnlySpan<char> value)
    {
        int start = 0;
        int end = value.Length;
        while (start < end && (value[start] == ' ' || value[start] == '\t'))
        {
            start++;
        }
        while (end > start && (value[end - 1] == ' ' || value[end - 1] == '\t'))
        {
            end--;
        }
        return value[start..end];
    }

    /// <summary>
    /// Finds the first occurrence of <paramref name="delimiter"/> in <paramref name="value"/>
    /// that is <em>not</em> inside a quoted-string (RFC 9110 &#167; 5.6.4). Backslash escapes
    /// inside a quoted-string are honored so an escaped quote does not prematurely close it.
    /// </summary>
    /// <param name="value">The span to scan.</param>
    /// <param name="delimiter">The delimiter to locate at the top level.</param>
    /// <returns>The index of the first unquoted delimiter, or <c>-1</c> when none exists.</returns>
    public static int IndexOfUnquoted(ReadOnlySpan<char> value, char delimiter)
    {
        bool inQuotes = false;
        for (int i = 0; i < value.Length; i++)
        {
            char c = value[i];
            if (inQuotes)
            {
                if (c == '\\' && i + 1 < value.Length)
                {
                    // Skip the escaped character.
                    i++;
                }
                else if (c == '"')
                {
                    inQuotes = false;
                }
            }
            else if (c == '"')
            {
                inQuotes = true;
            }
            else if (c == delimiter)
            {
                return i;
            }
        }
        return -1;
    }

    /// <summary>
    /// Determines whether <paramref name="value"/> is a well-formed RFC 9110 &#167; 5.6.4
    /// <c>quoted-string</c> that spans the entire span: it opens and closes with <c>"</c>, every
    /// backslash is followed by an escaped character, and the closing quote is the final character
    /// (an unterminated or prematurely-closed quote fails).
    /// </summary>
    /// <param name="value">The candidate value.</param>
    /// <returns><see langword="true"/> when the span is a complete quoted-string.</returns>
    public static bool IsQuotedString(ReadOnlySpan<char> value)
    {
        if (value.Length < 2 || value[0] != '"')
        {
            return false;
        }

        int i = 1;
        while (i < value.Length)
        {
            char c = value[i];
            if (c == '\\')
            {
                // A quoted-pair must have a character to escape; a trailing backslash is malformed.
                if (i + 1 >= value.Length)
                {
                    return false;
                }
                i += 2;
                continue;
            }
            if (c == '"')
            {
                // The closing quote is only valid as the final character.
                return i == value.Length - 1;
            }
            i++;
        }
        return false;
    }

    /// <summary>
    /// Unwraps an RFC 9110 &#167; 5.6.4 quoted-string, removing the surrounding quotes and
    /// resolving <c>\</c> escapes. When <paramref name="value"/> is not a quoted-string it is
    /// returned unchanged (already a bare token value).
    /// </summary>
    /// <param name="value">The raw parameter value (quoted or bare).</param>
    /// <returns>The unescaped textual value.</returns>
    public static string UnquoteValue(ReadOnlySpan<char> value)
    {
        if (value.Length < 2 || value[0] != '"' || value[^1] != '"')
        {
            return value.ToString();
        }

        ReadOnlySpan<char> inner = value[1..^1];
        if (inner.IndexOf('\\') < 0)
        {
            return inner.ToString();
        }

        Span<char> buffer = inner.Length <= 256 ? stackalloc char[inner.Length] : new char[inner.Length];
        int written = 0;
        for (int i = 0; i < inner.Length; i++)
        {
            char c = inner[i];
            if (c == '\\' && i + 1 < inner.Length)
            {
                buffer[written++] = inner[++i];
            }
            else
            {
                buffer[written++] = c;
            }
        }
        return new string(buffer[..written]);
    }
}

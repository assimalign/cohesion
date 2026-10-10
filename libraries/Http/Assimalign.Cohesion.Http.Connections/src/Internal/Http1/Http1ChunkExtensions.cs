using System;
using System.Buffers;

namespace Assimalign.Cohesion.Http.Connections.Internal;

/// <summary>
/// Checks the chunk extensions of an HTTP/1.1 chunk-size line against their grammar
/// (RFC 9112 §7.1.1):
/// <code>
/// chunk-ext      = *( BWS ";" BWS chunk-ext-name [ BWS "=" BWS chunk-ext-val ] )
/// chunk-ext-name = token
/// chunk-ext-val  = token / quoted-string
/// </code>
/// </summary>
/// <remarks>
/// The transport ignores chunk extensions, but it does not let them carry anything else. A chunk-size
/// line that another parser would end, or split, at a different octet than this one frames the body
/// differently: a bare LF in an extension, which an intermediary may take for the end of the line,
/// leaves the two disagreeing about where every later chunk starts, which is how requests are
/// smuggled. So every control character except a horizontal tab in a quoted-string or in the
/// whitespace between the parts fails the line, as does any other octet the grammar does not allow
/// where it stands.
/// </remarks>
internal static class Http1ChunkExtensions
{
    // RFC 9110 §5.6.2 — tchar, the characters of a token.
    private static readonly SearchValues<char> _tokenChars = SearchValues.Create(
        "!#$%&'*+-.^_`|~0123456789ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz");

    /// <summary>
    /// Whether <paramref name="extensions"/>, the part of a chunk-size line after the chunk-size,
    /// keeps to the chunk-ext grammar. An empty span does.
    /// </summary>
    /// <param name="extensions">The chunk extensions, without the chunk-size before them or the CRLF after them.</param>
    /// <returns><see langword="true"/> when every extension is well formed; otherwise <see langword="false"/>.</returns>
    public static bool IsWellFormed(ReadOnlySpan<char> extensions)
    {
        int position = 0;

        while (position < extensions.Length)
        {
            position = SkipWhitespace(extensions, position);

            if (position == extensions.Length || extensions[position] != ';')
            {
                return false;
            }

            position = SkipWhitespace(extensions, position + 1);

            if (!TrySkipToken(extensions, ref position))
            {
                return false;
            }

            // An optional "=" and value. Whitespace after the name belongs to the "=" when one follows;
            // otherwise it must lead to the next extension's ";", so whitespace that ends the line, after
            // a name as after a value, belongs to no extension and fails it.
            int afterName = position;
            position = SkipWhitespace(extensions, position);

            if (position < extensions.Length && extensions[position] == '=')
            {
                position = SkipWhitespace(extensions, position + 1);

                bool value = position < extensions.Length && extensions[position] == '"'
                    ? TrySkipQuotedString(extensions, ref position)
                    : TrySkipToken(extensions, ref position);

                if (!value)
                {
                    return false;
                }
            }
            else
            {
                position = afterName;
            }
        }

        return true;
    }

    // RFC 9110 §5.6.3 — BWS is OWS: spaces and horizontal tabs.
    private static int SkipWhitespace(ReadOnlySpan<char> text, int position)
    {
        while (position < text.Length && text[position] is ' ' or '\t')
        {
            position++;
        }

        return position;
    }

    private static bool TrySkipToken(ReadOnlySpan<char> text, ref int position)
    {
        int start = position;

        while (position < text.Length && _tokenChars.Contains(text[position]))
        {
            position++;
        }

        return position > start;
    }

    /// <summary>
    /// Skips a quoted-string (RFC 9110 §5.6.4) that starts at <paramref name="position"/>, its quotes
    /// included.
    /// </summary>
    private static bool TrySkipQuotedString(ReadOnlySpan<char> text, ref int position)
    {
        position++;

        while (position < text.Length)
        {
            char c = text[position];

            if (c == '"')
            {
                position++;
                return true;
            }

            if (c == '\\')
            {
                // quoted-pair = "\" ( HTAB / SP / VCHAR / obs-text )
                if (position + 1 == text.Length || !IsQuotedPairOctet(text[position + 1]))
                {
                    return false;
                }

                position += 2;
                continue;
            }

            // qdtext = HTAB / SP / %x21 / %x23-5B / %x5D-7E / obs-text
            if (!IsQuotedPairOctet(c))
            {
                return false;
            }

            position++;
        }

        return false;
    }

    // HTAB, SP, VCHAR (%x21-7E) or obs-text (%x80-FF): every octet but the other controls and DEL.
    // As qdtext it also excludes DQUOTE and "\", which the caller handles before asking.
    private static bool IsQuotedPairOctet(char c) => c is '\t' or (>= ' ' and <= '~') or (>= '\u0080' and <= 'ÿ');
}

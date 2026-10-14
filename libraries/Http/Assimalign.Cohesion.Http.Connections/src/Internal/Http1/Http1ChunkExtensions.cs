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
/// <para>
/// The checks are the core field rule (<see cref="HttpFieldNormalization"/>, #1341), the one the
/// header and trailer field lines get: a name or a token value is
/// <see cref="HttpFieldNormalization.IsValidFieldName"/>, and a quoted-string's content passes
/// <see cref="HttpFieldNormalization.IndexOfInvalidControlCharacter"/>. A bare CR or LF never
/// arrives here: the chunk framing line reader refuses it first.
/// </para>
/// </remarks>
internal static class Http1ChunkExtensions
{
    // The octets that end a token inside chunk-ext: BWS, the next extension's ';', and the '=' before
    // a value. Whatever stands before one of them must be a whole token.
    private static readonly SearchValues<char> _tokenDelimiters = SearchValues.Create(" \t;=");

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

    /// <summary>
    /// Skips the token (RFC 9110 §5.6.2) that starts at <paramref name="position"/>: the text up to
    /// the next BWS, ';' or '=' must be one, which the core field-name rule decides. Any other octet,
    /// a DQUOTE or a control character among them, fails the token.
    /// </summary>
    private static bool TrySkipToken(ReadOnlySpan<char> text, ref int position)
    {
        ReadOnlySpan<char> rest = text[position..];
        int length = rest.IndexOfAny(_tokenDelimiters);
        if (length < 0)
        {
            length = rest.Length;
        }

        if (!HttpFieldNormalization.IsValidFieldName(rest[..length]))
        {
            return false;
        }

        position += length;
        return true;
    }

    /// <summary>
    /// Skips a quoted-string (RFC 9110 §5.6.4) that starts at <paramref name="position"/>, its quotes
    /// included.
    /// </summary>
    /// <remarks>
    /// <c>qdtext = HTAB / SP / %x21 / %x23-5B / %x5D-7E / obs-text</c> and
    /// <c>quoted-pair = "\" ( HTAB / SP / VCHAR / obs-text )</c>: once the DQUOTE that ends the string
    /// and the backslash of each pair are set aside, every octet left is any octet but a control
    /// character other than HTAB, which is the core field-value rule. The line is decoded as Latin-1,
    /// so no character above U+00FF reaches it.
    /// </remarks>
    private static bool TrySkipQuotedString(ReadOnlySpan<char> text, ref int position)
    {
        int contentStart = position + 1;
        int index = contentStart;

        while (index < text.Length)
        {
            char c = text[index];

            if (c == '"')
            {
                if (HttpFieldNormalization.IndexOfInvalidControlCharacter(text[contentStart..index]) >= 0)
                {
                    return false;
                }

                position = index + 1;
                return true;
            }

            // A quoted-pair takes the octet after its backslash whatever it is, a DQUOTE included; a
            // backslash that ends the line leaves the string unclosed.
            index += c == '\\' ? 2 : 1;
        }

        return false;
    }
}

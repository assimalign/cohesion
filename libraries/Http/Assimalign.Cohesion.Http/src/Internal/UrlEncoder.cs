using System;
using System.Buffers;
using System.Text;

namespace Assimalign.Cohesion.Http.Internal;

/// <summary>
/// Percent-encoding (RFC 3986 §2.1) for the URI components the core value objects write back out:
/// the inverse of <see cref="UrlDecoder"/> for a path, and of <see cref="HttpQuery.Parse"/> for a query.
/// </summary>
internal static class UrlEncoder
{
    // RFC 3986 §2.3.
    private const string Unreserved = "ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz0123456789-._~";

    // RFC 3986 §2.2.
    private const string SubDelimiters = "!$&'()*+,;=";

    // A path: pchar (RFC 3986 §3.3: unreserved, sub-delims, ':' and '@') and the '/' between segments.
    private static readonly SearchValues<char> _pathCharacters = SearchValues.Create(Unreserved + SubDelimiters + ":@/");

    // A query name or value: everything but unreserved is escaped, so no '&', '=' or '+' in the text can
    // split or reinterpret the entry it belongs to.
    private static readonly SearchValues<char> _componentCharacters = SearchValues.Create(Unreserved);

    private static ReadOnlySpan<char> HexDigits => "0123456789ABCDEF";

    /// <summary>
    /// Encodes a percent-decoded path so that <see cref="HttpPath.FromUriComponent(string)"/> reads it back
    /// unchanged. Every character outside <c>pchar</c> and <c>/</c> is percent-encoded as UTF-8, and a
    /// <c>%</c> becomes <c>%25</c> unless it begins an encoded slash, which the decoder never decodes and
    /// the path therefore already carries in encoded form.
    /// </summary>
    public static string EncodePath(string path)
    {
        ReadOnlySpan<char> text = path;
        int index = text.IndexOfAnyExcept(_pathCharacters);
        if (index < 0)
        {
            return path;
        }

        StringBuilder builder = new(path.Length + 16);

        while (true)
        {
            builder.Append(text[..index]);
            text = text[index..];

            if (text[0] == '%')
            {
                // UrlDecoder leaves "%2F" (either case) as written, so one in a decoded path is the encoded
                // slash the request carried, never a '%' followed by "2F". Encoding its '%' would write
                // "%252F": the same decoded path, but a different URI.
                if (IsEncodedSlash(text))
                {
                    builder.Append(text[..3]);
                    text = text[3..];
                }
                else
                {
                    builder.Append("%25");
                    text = text[1..];
                }
            }
            else
            {
                text = AppendRune(builder, text);
            }

            index = text.IndexOfAnyExcept(_pathCharacters);
            if (index < 0)
            {
                builder.Append(text);
                return builder.ToString();
            }
        }
    }

    /// <summary>
    /// Appends a query name or value, percent-encoding (UTF-8) every character that is not unreserved.
    /// </summary>
    public static void AppendComponent(StringBuilder builder, ReadOnlySpan<char> text)
    {
        while (!text.IsEmpty)
        {
            int index = text.IndexOfAnyExcept(_componentCharacters);
            if (index < 0)
            {
                builder.Append(text);
                return;
            }

            builder.Append(text[..index]);
            text = AppendRune(builder, text[index..]);
        }
    }

    private static bool IsEncodedSlash(ReadOnlySpan<char> text)
        => text.Length >= 3 && text[1] == '2' && (text[2] == 'F' || text[2] == 'f');

    // Percent-encodes the scalar value at the start of the text as UTF-8 and returns the rest. A lone
    // surrogate is not text; it is encoded as U+FFFD, as UTF-8 encoders do.
    private static ReadOnlySpan<char> AppendRune(StringBuilder builder, ReadOnlySpan<char> text)
    {
        if (Rune.DecodeFromUtf16(text, out Rune rune, out int consumed) != OperationStatus.Done)
        {
            rune = Rune.ReplacementChar;
            consumed = Math.Max(consumed, 1);
        }

        Span<byte> utf8 = stackalloc byte[4];
        int length = rune.EncodeToUtf8(utf8);

        for (int i = 0; i < length; i++)
        {
            byte value = utf8[i];
            builder.Append('%').Append(HexDigits[value >> 4]).Append(HexDigits[value & 0xF]);
        }

        return text[consumed..];
    }
}

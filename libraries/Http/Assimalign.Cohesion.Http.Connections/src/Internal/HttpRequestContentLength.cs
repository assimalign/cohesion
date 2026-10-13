using System;
using System.Globalization;
using System.IO;

namespace Assimalign.Cohesion.Http.Connections.Internal;

/// <summary>
/// Reads the <c>Content-Length</c> a request declares on HTTP/2 and HTTP/3, whose framing is the
/// stream's own (RFC 9113 §8.1, RFC 9114 §4.1): the declaration is checked against the content the
/// stream carries, and a request whose content contradicts it is malformed (RFC 9113 §8.1.1,
/// RFC 9114 §4.1.2). HTTP/1.1 frames its body by the declaration itself and reads it in
/// <see cref="Http1MessageBodyReader"/>.
/// </summary>
internal static class HttpRequestContentLength
{
    /// <summary>
    /// Parses the request's <c>Content-Length</c>, if any (RFC 9110 §8.6): one or more identical
    /// non-negative decimal values, as separate field lines or a comma-separated list whose elements lose
    /// SP and HTAB and nothing else.
    /// </summary>
    /// <param name="headers">The request's decoded header section.</param>
    /// <param name="protocol">The protocol named in a failure's message, such as <c>HTTP/2</c>.</param>
    /// <returns>The declared length, or <see langword="null"/> when the request declares none.</returns>
    /// <exception cref="InvalidDataException">
    /// The field is empty, holds a value that is not a non-negative decimal integer, or holds two different
    /// values: the request is malformed.
    /// </exception>
    public static long? Parse(HttpHeaderCollection headers, string protocol)
    {
        if (!headers.TryGetValue(HttpHeaderKey.ContentLength, out HttpHeaderValue raw))
        {
            return null;
        }

        long? agreed = null;

        foreach (string? entry in raw)
        {
            if (entry is null)
            {
                continue;
            }

            // RFC 9110 §5.6.1 / §5.6.3 — trim OWS, SP and HTAB, from each element and nothing else, as HTTP/1.1
            // does (#1341). StringSplitOptions.TrimEntries also strips U+0085 and U+00A0, which QPACK and HPACK
            // decode from obs-text, so "5\xA0" was enforced as 5 while HTTP/1.1 answered it with 400 and a hop
            // that compares the value exactly saw no valid length.
            ReadOnlySpan<char> list = entry.AsSpan();
            foreach (Range range in list.Split(','))
            {
                ReadOnlySpan<char> element = list[range].Trim(Http1FieldLine.OptionalWhitespace);
                if (element.IsEmpty)
                {
                    continue;
                }

                if (!IsAsciiDigits(element)
                    || !long.TryParse(element, NumberStyles.None, CultureInfo.InvariantCulture, out long parsed))
                {
                    throw new InvalidDataException(string.Format(CultureInfo.CurrentCulture, ErrorMessages.RequestContentLengthValueIsNotANonNegativeDecimalIntegerRfc, protocol, element.ToString()));
                }

                if (agreed is { } existing && existing != parsed)
                {
                    throw new InvalidDataException(string.Format(CultureInfo.CurrentCulture, ErrorMessages.RequestDeclaresConflictingContentLengthValuesAndRfc911086, protocol, existing, parsed));
                }

                agreed = parsed;
            }
        }

        return agreed ?? throw new InvalidDataException(string.Format(CultureInfo.CurrentCulture, ErrorMessages.RequestCarriesAnEmptyContentLengthFieldRfc911086, protocol));
    }

    private static bool IsAsciiDigits(ReadOnlySpan<char> value)
    {
        foreach (char c in value)
        {
            if (c is < '0' or > '9')
            {
                return false;
            }
        }

        return !value.IsEmpty;
    }
}

using System;
using System.IO;

namespace Assimalign.Cohesion.Http.Connections.Internal.Http1;

/// <summary>
/// Decides how an HTTP/1.1 request body is framed (RFC 9112 §6 / §7) from the request headers,
/// rejecting every ambiguous or malformed combination defined as a request-smuggling vector by
/// RFC 9112 §6.3 <em>before</em> the request is dispatched. The framing decision is handed to
/// <see cref="Http1RequestBodyStream"/>, which performs the incremental read (and enforces the
/// body-size cap and data rate as the body flows).
/// </summary>
/// <remarks>
/// <para>
/// Validation rules enforced here (all map to RFC 9112 §6.3 "Bad Request" and drop the connection
/// before any body byte is read):
/// </para>
/// <list type="bullet">
///   <item><description>A message MUST NOT contain both a <c>Content-Length</c> header and a
///   <c>Transfer-Encoding</c> header.</description></item>
///   <item><description><c>Content-Length</c> values must be a non-negative ASCII decimal. Leading
///   signs, decimal points, hex, and embedded whitespace are rejected.</description></item>
///   <item><description>Multiple <c>Content-Length</c> headers (or a single header with a
///   comma-separated list) must all carry the same value; mismatched values are rejected.</description></item>
///   <item><description>For HTTP/1.1 the last (or only) transfer coding MUST be <c>chunked</c>.</description></item>
/// </list>
/// <para>
/// The body-size cap is deliberately <em>not</em> checked here: an endpoint or middleware may still
/// raise or lower the per-request cap after dispatch (before the body is read), so the cap is
/// enforced by <see cref="Http1RequestBodyStream"/> at the first read against the frozen value.
/// </para>
/// </remarks>
internal static class Http1MessageBodyReader
{
    /// <summary>
    /// Determines the request-body framing from the parsed request headers.
    /// </summary>
    /// <param name="headers">The already-parsed request header collection.</param>
    /// <returns>The framing decision.</returns>
    /// <exception cref="InvalidDataException">Thrown when the framing headers are ambiguous or malformed (RFC 9112 §6.3 / §7.4).</exception>
    public static Http1RequestBodyFraming DetermineFraming(HttpHeaderCollection headers)
    {
        bool hasTransferEncoding = headers.ContainsKey(HttpHeaderKey.TransferEncoding);
        bool hasContentLength = headers.ContainsKey(HttpHeaderKey.ContentLength);

        if (hasTransferEncoding && hasContentLength)
        {
            throw new InvalidDataException(
                "RFC 9112 §6.3: message MUST NOT contain both Content-Length and Transfer-Encoding headers.");
        }

        if (hasTransferEncoding)
        {
            ValidateTransferEncoding(headers);
            return Http1RequestBodyFraming.Chunked;
        }

        if (hasContentLength)
        {
            return Http1RequestBodyFraming.ForContentLength(ParseContentLength(headers));
        }

        // RFC 9112 §6.1 — absent both framing headers, the request body is empty.
        return Http1RequestBodyFraming.None;
    }

    private static void ValidateTransferEncoding(HttpHeaderCollection headers)
    {
        if (!headers.TryGetValue(HttpHeaderKey.TransferEncoding, out HttpHeaderValue te))
        {
            return;
        }

        // RFC 9112 §7.4 — for HTTP/1.1 the last (or only) coding MUST be chunked. Cohesion only
        // supports identity + chunked today; reject anything else (gzip / deflate / compress are
        // content codings, not transfer codings, and don't belong here).
        string? last = null;
        foreach (string? entry in te)
        {
            if (entry is null)
            {
                continue;
            }

            foreach (string segment in entry.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            {
                last = segment;
            }
        }
        if (last is null || !string.Equals(last, "chunked", StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException(
                $"RFC 9112 §7.4: Transfer-Encoding MUST end with 'chunked' for HTTP/1.1 requests; got '{te.Value}'.");
        }
    }

    private static long ParseContentLength(HttpHeaderCollection headers)
    {
        if (!headers.TryGetValue(HttpHeaderKey.ContentLength, out HttpHeaderValue raw))
        {
            return 0;
        }

        // RFC 9112 §6.3 — Content-Length may appear as a single header, multiple headers, or a
        // comma-separated list. All values must be identical, or the server MUST reject the message.
        long? agreed = null;
        foreach (string? entry in raw)
        {
            if (entry is null)
            {
                continue;
            }

            foreach (string segment in entry.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            {
                if (!IsAllAsciiDigits(segment))
                {
                    throw new InvalidDataException(
                        $"RFC 9112 §6.3: Content-Length value '{segment}' is not a non-negative ASCII decimal.");
                }
                if (!long.TryParse(segment, System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out long parsed))
                {
                    throw new InvalidDataException(
                        $"RFC 9112 §6.3: Content-Length value '{segment}' could not be parsed as a non-negative integer.");
                }
                if (parsed < 0)
                {
                    throw new InvalidDataException(
                        $"RFC 9112 §6.3: Content-Length value '{segment}' is negative.");
                }
                if (agreed is { } existing && existing != parsed)
                {
                    throw new InvalidDataException(
                        $"RFC 9112 §6.3: conflicting Content-Length values ({existing} and {parsed}) were declared.");
                }
                agreed = parsed;
            }
        }

        // The header was present (TryGetValue above succeeded) but every segment was dropped by
        // Split's RemoveEmptyEntries → the value was empty / whitespace-only. RFC 9112 §6.3 — an
        // empty Content-Length is not a valid declaration.
        return agreed ?? throw new InvalidDataException(
            "RFC 9112 §6.3: Content-Length header was present but contained no value.");
    }

    private static bool IsAllAsciiDigits(string value)
    {
        if (string.IsNullOrEmpty(value))
        {
            return false;
        }
        foreach (char c in value)
        {
            if (c < '0' || c > '9')
            {
                return false;
            }
        }
        return true;
    }
}

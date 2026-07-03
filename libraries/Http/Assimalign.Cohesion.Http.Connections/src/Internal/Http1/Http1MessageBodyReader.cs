using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace Assimalign.Cohesion.Http.Connections.Internal.Http1;

/// <summary>
/// Reads an HTTP/1.1 message body from the wire per RFC 9112 §6 / §7 framing rules.
/// Handles the three legal framing strategies (Transfer-Encoding: chunked,
/// Content-Length, and no body) and rejects every ambiguous or malformed
/// combination defined as a request-smuggling vector by RFC 9112 §6.3.
/// </summary>
/// <remarks>
/// <para>
/// Validation rules enforced (all map to RFC 9112 §6.3 "Bad Request" responses):
/// </para>
/// <list type="bullet">
///   <item><description>A message MUST NOT contain both a <c>Content-Length</c> header
///   and a <c>Transfer-Encoding</c> header.</description></item>
///   <item><description><c>Content-Length</c> values must be a non-negative ASCII
///   decimal. Leading signs, decimal points, hex, and embedded whitespace are
///   rejected.</description></item>
///   <item><description>Multiple <c>Content-Length</c> headers (or a single header with
///   a comma-separated list) must all carry the same value; mismatched values are
///   rejected.</description></item>
///   <item><description>Chunk sizes must parse as ASCII hex with no leading sign or
///   whitespace. Negative / out-of-range sizes are rejected.</description></item>
///   <item><description>Chunk-ext fields are stripped per RFC 9112 §7.1.1 (a recipient
///   MAY ignore them). They are not surfaced to the application layer.</description></item>
///   <item><description>The body is capped at the effective per-request maximum body size
///   (<c>maxBodySize</c>, sourced from <see cref="HttpServerLimits.MaxRequestBodySize"/> and the
///   per-request <see cref="Assimalign.Cohesion.Http.IHttpMaxRequestBodySizeFeature"/>) so a
///   malicious peer cannot send Content-Length: 2^63 and exhaust the heap. A <see langword="null"/>
///   cap leaves the body unbounded; an exceeded cap is rejected with 413.</description></item>
/// </list>
/// </remarks>
internal static class Http1MessageBodyReader
{
    /// <summary>
    /// Reads the message body per the framing rules signalled by <paramref name="headers"/>,
    /// enforcing the effective per-request body-size cap.
    /// </summary>
    /// <param name="stream">The connection stream to read from.</param>
    /// <param name="headers">The already-parsed request header collection.</param>
    /// <param name="maxBodySize">The maximum body size in octets, or <see langword="null"/> for unbounded.</param>
    /// <param name="cancellationToken">A token to cancel the read.</param>
    /// <returns>The parsed message body and any chunked trailer fields.</returns>
    /// <exception cref="Http1LimitExceededException">Thrown (413) when the body exceeds <paramref name="maxBodySize"/>.</exception>
    public static async ValueTask<Http1MessageBody> ReadAsync(
        Stream stream,
        HttpHeaderCollection headers,
        long? maxBodySize,
        CancellationToken cancellationToken)
    {
        // RFC 9112 §6.3 — reject ambiguous framing before reading a single body byte.
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
            return await ReadChunkedAsync(stream, maxBodySize, cancellationToken).ConfigureAwait(false);
        }

        if (hasContentLength)
        {
            long length = ParseContentLength(headers, maxBodySize);
            byte[] body = await ReadFixedLengthAsync(stream, length, maxBodySize, cancellationToken).ConfigureAwait(false);
            return new Http1MessageBody(body, new HttpHeaderCollection());
        }

        // No framing → no body. RFC 9112 §6.1 — absent both, the request body is empty.
        return new Http1MessageBody(Array.Empty<byte>(), new HttpHeaderCollection());
    }

    private static void ValidateTransferEncoding(HttpHeaderCollection headers)
    {
        if (!headers.TryGetValue(HttpHeaderKey.TransferEncoding, out HttpHeaderValue te))
        {
            return;
        }

        // RFC 9112 §7.4 — for HTTP/1.1 the last (or only) coding MUST be chunked. Cohesion
        // only supports identity + chunked today; reject anything else (gzip / deflate /
        // compress are content codings, not transfer codings, and don't belong here).
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

    private static long ParseContentLength(HttpHeaderCollection headers, long? maxBodySize)
    {
        if (!headers.TryGetValue(HttpHeaderKey.ContentLength, out HttpHeaderValue raw))
        {
            return 0;
        }

        // RFC 9112 §6.3 — Content-Length may appear as a single header, multiple
        // headers, or a comma-separated list. All values must be identical, or the
        // server MUST reject the message.
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
                if (maxBodySize is { } cap && parsed > cap)
                {
                    // RFC 9110 §15.5.14 — reject an oversized declaration before reading a byte.
                    throw new Http1LimitExceededException(
                        HttpStatusCode.RequestEntityTooLarge,
                        $"Content-Length value '{segment}' exceeds the configured maximum request body size ({cap} octets).");
                }
                if (agreed is { } existing && existing != parsed)
                {
                    throw new InvalidDataException(
                        $"RFC 9112 §6.3: conflicting Content-Length values ({existing} and {parsed}) were declared.");
                }
                agreed = parsed;
            }
        }
        // The header was present (TryGetValue above succeeded) but every segment was
        // dropped by Split's RemoveEmptyEntries → the value was empty / whitespace-only.
        // RFC 9112 §6.3 — an empty Content-Length is not a valid declaration.
        return agreed ?? throw new InvalidDataException(
            "RFC 9112 §6.3: Content-Length header was present but contained no value.");
    }

    private static async ValueTask<byte[]> ReadFixedLengthAsync(
        Stream stream,
        long length,
        long? maxBodySize,
        CancellationToken cancellationToken)
    {
        if (length == 0)
        {
            return Array.Empty<byte>();
        }
        if (maxBodySize is { } cap && length > cap)
        {
            // Defense in depth — ParseContentLength already rejects this.
            throw new Http1LimitExceededException(
                HttpStatusCode.RequestEntityTooLarge,
                $"Declared body length {length} exceeds the configured maximum request body size ({cap} octets).");
        }

        byte[] body = new byte[length];
        int offset = 0;
        while (offset < body.Length)
        {
            int read = await stream.ReadAsync(body.AsMemory(offset, body.Length - offset), cancellationToken).ConfigureAwait(false);
            if (read == 0)
            {
                throw new EndOfStreamException(
                    $"The connection closed after {offset} of {body.Length} expected body octets.");
            }
            offset += read;
        }
        return body;
    }

    private static async ValueTask<Http1MessageBody> ReadChunkedAsync(
        Stream stream,
        long? maxBodySize,
        CancellationToken cancellationToken)
    {
        using MemoryStream body = new();
        long totalRead = 0;

        while (true)
        {
            string? sizeLine = await ReadLineAsync(stream, cancellationToken).ConfigureAwait(false);
            if (sizeLine is null)
            {
                throw new EndOfStreamException(
                    "RFC 9112 §7.1: connection closed before a chunk-size line was received.");
            }

            // RFC 9112 §7.1.1 — chunk-ext is optional ";<ext-name>[=<ext-val>]" appended
            // to the size. Strip it and pass the size portion through to the strict hex
            // parser. BWS is allowed between chunk-size and the ';' so we trim trailing
            // whitespace when an extension is present, but leading whitespace is rejected
            // (the grammar is `chunk-size = 1*HEXDIG`, no LWS prefix).
            int semicolon = sizeLine.IndexOf(';');
            ReadOnlySpan<char> sizeText = semicolon < 0
                ? sizeLine.AsSpan()
                : sizeLine.AsSpan(0, semicolon).TrimEnd();

            int chunkSize = ParseChunkSize(sizeText);

            if (chunkSize == 0)
            {
                // Last chunk — followed by an optional trailer section and an empty line.
                HttpHeaderCollection trailers = await ReadTrailersAsync(stream, cancellationToken).ConfigureAwait(false);
                return new Http1MessageBody(body.ToArray(), trailers);
            }

            if (maxBodySize is { } cap && totalRead + chunkSize > cap)
            {
                // RFC 9110 §15.5.14 — a chunked body that would exceed the cap is rejected as it
                // accumulates, before the offending chunk is buffered.
                throw new Http1LimitExceededException(
                    HttpStatusCode.RequestEntityTooLarge,
                    $"Chunked body exceeds the configured maximum request body size ({cap} octets) at chunk size {chunkSize} after {totalRead} octets read.");
            }

            byte[] buffer = new byte[chunkSize];
            int offset = 0;
            while (offset < chunkSize)
            {
                int read = await stream.ReadAsync(buffer.AsMemory(offset, chunkSize - offset), cancellationToken).ConfigureAwait(false);
                if (read == 0)
                {
                    throw new EndOfStreamException(
                        $"RFC 9112 §7.1: connection closed mid-chunk after {offset} of {chunkSize} octets.");
                }
                offset += read;
            }
            await body.WriteAsync(buffer, cancellationToken).ConfigureAwait(false);
            totalRead += chunkSize;

            // RFC 9112 §7.1 — every chunk is terminated by CRLF.
            string? terminator = await ReadLineAsync(stream, cancellationToken).ConfigureAwait(false);
            if (terminator is null)
            {
                throw new EndOfStreamException(
                    "RFC 9112 §7.1: connection closed before a chunk terminator was received.");
            }
            if (terminator.Length != 0)
            {
                throw new InvalidDataException(
                    $"RFC 9112 §7.1: chunk terminator must be CRLF only; got '{terminator}'.");
            }
        }
    }

    private static int ParseChunkSize(ReadOnlySpan<char> sizeText)
    {
        if (sizeText.IsEmpty)
        {
            throw new InvalidDataException("RFC 9112 §7.1: empty chunk-size.");
        }
        // chunk-size = 1*HEXDIG — accept ASCII hex only, no leading sign, no whitespace.
        int value = 0;
        foreach (char c in sizeText)
        {
            int digit;
            if (c >= '0' && c <= '9')
            {
                digit = c - '0';
            }
            else if (c >= 'a' && c <= 'f')
            {
                digit = c - 'a' + 10;
            }
            else if (c >= 'A' && c <= 'F')
            {
                digit = c - 'A' + 10;
            }
            else
            {
                throw new InvalidDataException(
                    $"RFC 9112 §7.1: chunk-size '{sizeText.ToString()}' contains non-hex character '{c}'.");
            }

            // Overflow guard: a single chunk-size is bounded by Int32; the accumulated body is
            // separately bounded by the effective max-body-size cap in ReadChunkedAsync.
            if (value > (int.MaxValue - digit) / 16)
            {
                throw new InvalidDataException(
                    $"RFC 9112 §7.1: chunk-size '{sizeText.ToString()}' overflows Int32.");
            }
            value = (value * 16) + digit;
        }
        return value;
    }

    private static async ValueTask<HttpHeaderCollection> ReadTrailersAsync(
        Stream stream,
        CancellationToken cancellationToken)
    {
        // RFC 9112 §7.1.2 — trailer-section follows the last chunk and is terminated by an
        // empty line. The shape is identical to the header section.
        HttpHeaderCollection trailers = new();

        while (true)
        {
            string? line = await ReadLineAsync(stream, cancellationToken).ConfigureAwait(false);
            if (line is null)
            {
                throw new EndOfStreamException(
                    "RFC 9112 §7.1.2: connection closed before the trailer section terminator.");
            }
            if (line.Length == 0)
            {
                return trailers;
            }

            int colon = line.IndexOf(':');
            if (colon <= 0)
            {
                throw new InvalidDataException(
                    $"RFC 9112 §7.1.2: malformed trailer line '{line}'.");
            }

            string name = line[..colon].Trim();
            string value = line[(colon + 1)..].Trim();
            HttpHeaderKey key = new(name);

            // RFC 9112 §7.1.2 forbids certain headers in trailers (Content-Length,
            // Transfer-Encoding, Host, auth headers, etc.). Conservatively reject the
            // framing-related ones — letting them through would be a smuggling vector.
            if (key.Equals(HttpHeaderKey.ContentLength)
                || key.Equals(HttpHeaderKey.TransferEncoding)
                || key.Equals(HttpHeaderKey.Host))
            {
                throw new InvalidDataException(
                    $"RFC 9112 §7.1.2: trailer field '{name}' is forbidden in the trailer section.");
            }

            if (trailers.TryGetValue(key, out HttpHeaderValue existing))
            {
                trailers[key] = HttpHeaderValue.Concat(existing, value);
            }
            else
            {
                trailers[key] = value;
            }
        }
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

    private static async ValueTask<string?> ReadLineAsync(Stream stream, CancellationToken cancellationToken)
    {
        using MemoryStream buffer = new();
        bool sawCr = false;

        while (true)
        {
            int b = await ReadByteAsync(stream, cancellationToken).ConfigureAwait(false);
            if (b < 0)
            {
                if (buffer.Length == 0 && !sawCr)
                {
                    return null;
                }
                throw new EndOfStreamException("The connection closed while an HTTP line was being read.");
            }

            if (sawCr)
            {
                if (b == '\n')
                {
                    return Encoding.ASCII.GetString(buffer.GetBuffer(), 0, (int)buffer.Length);
                }
                buffer.WriteByte((byte)'\r');
                sawCr = false;
            }

            if (b == '\r')
            {
                sawCr = true;
                continue;
            }

            buffer.WriteByte((byte)b);
        }
    }

    private static async ValueTask<int> ReadByteAsync(Stream stream, CancellationToken cancellationToken)
    {
        byte[] buf = new byte[1];
        int n = await stream.ReadAsync(buf.AsMemory(0, 1), cancellationToken).ConfigureAwait(false);
        return n == 0 ? -1 : buf[0];
    }
}

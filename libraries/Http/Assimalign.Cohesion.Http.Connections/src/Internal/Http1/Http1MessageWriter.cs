using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

namespace Assimalign.Cohesion.Http.Connections.Internal.Http1;

internal static class Http1MessageWriter
{
    /// <summary>
    /// Writes a minimal, bodyless HTTP/1.1 error response (status line, zero Content-Length, and
    /// <c>Connection: close</c>) directly to the stream. Used by the read path to emit a
    /// protocol-level rejection (414 / 431 / 413 / 408) for a request that never became a valid
    /// <see cref="Http1Context"/>, before the connection is closed.
    /// </summary>
    /// <param name="stream">The connection stream to write to.</param>
    /// <param name="statusCode">The status code to emit.</param>
    /// <param name="cancellationToken">A token to cancel the write.</param>
    /// <returns>A task that completes when the response has been flushed.</returns>
    public static async ValueTask WriteErrorResponseAsync(Stream stream, HttpStatusCode statusCode, CancellationToken cancellationToken)
    {
        await WriteAsciiAsync(stream, $"HTTP/1.1 {statusCode}\r\n", cancellationToken).ConfigureAwait(false);
        await WriteAsciiAsync(stream, "Content-Length: 0\r\n", cancellationToken).ConfigureAwait(false);
        await WriteAsciiAsync(stream, "Connection: close\r\n", cancellationToken).ConfigureAwait(false);
        await WriteAsciiAsync(stream, "\r\n", cancellationToken).ConfigureAwait(false);
        await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Writes an interim (<c>1xx</c>) HTTP/1.1 response — a status line, an optional set of field
    /// lines, and the blank line terminating the head — then flushes. An interim response carries no
    /// body, so no <c>Content-Length</c> is synthesized. Used both by the automatic
    /// <c>Expect: 100-continue</c> handshake (RFC 9110 §10.1.1) and by
    /// <see cref="Http1InterimResponseFeature"/> (100 Continue on demand, 103 Early Hints with
    /// <c>Link</c> fields). The connection stays live: interim responses precede the final response
    /// on the same exchange.
    /// </summary>
    /// <param name="stream">The connection stream to write to.</param>
    /// <param name="statusCode">The interim status code (validated by the caller to be 1xx, not 101).</param>
    /// <param name="headers">The interim field lines, or <see langword="null"/> for none.</param>
    /// <param name="cancellationToken">A token to cancel the write.</param>
    /// <returns>A task that completes when the interim response has been flushed.</returns>
    public static async ValueTask WriteInterimResponseAsync(
        Stream stream,
        HttpStatusCode statusCode,
        IHttpHeaderCollection? headers,
        CancellationToken cancellationToken)
    {
        await WriteAsciiAsync(stream, $"HTTP/1.1 {statusCode}\r\n", cancellationToken).ConfigureAwait(false);

        if (headers is not null)
        {
            foreach (System.Collections.Generic.KeyValuePair<HttpHeaderKey, HttpHeaderValue> header in headers)
            {
                // Emit one field line per value so a multi-valued field (e.g. several Link relations
                // in a 103 Early Hints) is expressed without comma-folding — always valid HTTP.
                foreach (string? value in header.Value)
                {
                    if (!string.IsNullOrEmpty(value))
                    {
                        await WriteAsciiAsync(stream, $"{header.Key}: {value}\r\n", cancellationToken).ConfigureAwait(false);
                    }
                }
            }
        }

        await WriteAsciiAsync(stream, "\r\n", cancellationToken).ConfigureAwait(false);
        await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
    }

    public static async ValueTask WriteResponseAsync(Stream stream, Http1Context context, CancellationToken cancellationToken)
    {
        byte[] bodyBytes = await ReadBodyAsync(context.Response.Body, cancellationToken).ConfigureAwait(false);
        HttpHeaderCollection headers = context.Response.Headers;

        if (!headers.ContainsKey(HttpHeaderKey.ContentLength))
        {
            headers[HttpHeaderKey.ContentLength] = bodyBytes.Length.ToString(System.Globalization.CultureInfo.InvariantCulture);
        }

        if (!context.KeepAlive)
        {
            headers[HttpHeaderKey.Connection] = "close";
        }

        await WriteHeadAsync(stream, context.Response.StatusCode, headers, cancellationToken).ConfigureAwait(false);

        if (context.Request.Method != HttpMethod.Head && bodyBytes.Length > 0)
        {
            await stream.WriteAsync(bodyBytes, 0, bodyBytes.Length, cancellationToken).ConfigureAwait(false);
        }

        await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Writes the response head — the status line, every header field line (with
    /// RFC 6265 one-line-per-cookie handling for <c>Set-Cookie</c>), and the blank
    /// line terminating the header section — without a trailing flush and without
    /// writing any body. Shared by the buffered response path and the incremental
    /// streaming sink (<see cref="Http1ResponseBodyStream"/>) so both commit
    /// headers identically.
    /// </summary>
    /// <param name="stream">The connection stream to write to.</param>
    /// <param name="statusCode">The response status code.</param>
    /// <param name="headers">The response headers to emit.</param>
    /// <param name="cancellationToken">A token to cancel the write.</param>
    /// <returns>A task that completes when the head bytes have been written to the stream buffer.</returns>
    public static async ValueTask WriteHeadAsync(Stream stream, HttpStatusCode statusCode, HttpHeaderCollection headers, CancellationToken cancellationToken)
    {
        // RFC 9110 §15.2 — a 1xx status is never a valid final response status. The one 1xx that ends
        // an exchange (101 Switching Protocols) is finalized out-of-band by the protocol-upgrade path,
        // whose send is suppressed, so it never reaches this shared final-head writer.
        HttpInterimResponseRules.EnsureFinalStatusCode(statusCode);

        await WriteAsciiAsync(stream, $"HTTP/1.1 {statusCode}\r\n", cancellationToken).ConfigureAwait(false);

        foreach (System.Collections.Generic.KeyValuePair<HttpHeaderKey, HttpHeaderValue> header in headers)
        {
            // RFC 6265 §3 — Set-Cookie MUST be emitted as one field line per
            // value; combining cookies into a single comma-separated value is
            // forbidden. Every other header is comma-folded as usual.
            if (header.Key == HttpHeaderKey.SetCookie)
            {
                foreach (string? value in header.Value)
                {
                    if (!string.IsNullOrEmpty(value))
                    {
                        await WriteAsciiAsync(stream, $"{header.Key}: {value}\r\n", cancellationToken).ConfigureAwait(false);
                    }
                }
            }
            else
            {
                await WriteAsciiAsync(stream, $"{header.Key}: {header.Value}\r\n", cancellationToken).ConfigureAwait(false);
            }
        }

        await WriteAsciiAsync(stream, "\r\n", cancellationToken).ConfigureAwait(false);
    }

    private static async ValueTask<byte[]> ReadBodyAsync(Stream body, CancellationToken cancellationToken)
    {
        if (body is MemoryStream memoryStream)
        {
            return memoryStream.ToArray();
        }

        if (body.CanSeek)
        {
            long originalPosition = body.Position;
            body.Position = 0;

            using MemoryStream buffer = new();
            await body.CopyToAsync(buffer, cancellationToken).ConfigureAwait(false);

            body.Position = originalPosition;

            return buffer.ToArray();
        }

        using MemoryStream copy = new();
        await body.CopyToAsync(copy, cancellationToken).ConfigureAwait(false);
        return copy.ToArray();
    }

    private static ValueTask WriteAsciiAsync(Stream stream, string value, CancellationToken cancellationToken)
    {
        byte[] buffer = System.Text.Encoding.ASCII.GetBytes(value);
        return new ValueTask(stream.WriteAsync(buffer, 0, buffer.Length, cancellationToken));
    }
}

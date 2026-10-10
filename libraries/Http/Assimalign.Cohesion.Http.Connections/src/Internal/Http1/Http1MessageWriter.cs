using System;
using System.Buffers;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace Assimalign.Cohesion.Http.Connections.Internal;

/// <summary>
/// Serializes HTTP/1.1 response heads and buffered responses.
/// </summary>
/// <remarks>
/// A head is encoded whole, in memory, before any of it is written, and each field line is checked
/// against the field syntax as it is encoded (<see cref="HttpResponseFieldRules"/>, #1183). A field
/// whose name is not a token, or whose value holds CR, LF, NUL, or another control character but HTAB,
/// throws <see cref="HttpInvalidResponseFieldException"/> with nothing on the wire, so a value can never
/// end its field line early and start a header, or a second response, of its own (CWE-113). HTTP/1.1
/// sends no response trailers (decision 18), so a head is the only field section this writer emits.
/// </remarks>
internal static class Http1MessageWriter
{
    private const string HttpVersionPrefix = "HTTP/1.1 ";
    private const string LineEnd = "\r\n";
    private const string FieldSeparator = ": ";

    // A typical head fits; a larger one grows the buffer.
    private const int InitialHeadCapacity = 512;

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
    /// <exception cref="HttpInvalidResponseFieldException">
    /// A field name is not a token, or a value holds a control character other than HTAB. Nothing was written.
    /// </exception>
    public static async ValueTask WriteInterimResponseAsync(
        Stream stream,
        HttpStatusCode statusCode,
        IHttpHeaderCollection? headers,
        CancellationToken cancellationToken)
    {
        ReadOnlyMemory<byte> head = EncodeInterimHead(statusCode, headers);

        await stream.WriteAsync(head, cancellationToken).ConfigureAwait(false);
        await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Prepares a buffered response: reads its body, completes the head's framing fields
    /// (<c>Content-Length</c>, and <c>Connection: close</c> when the connection will not be kept
    /// alive), and encodes the head. Nothing is written, so a refused field leaves the response
    /// unstarted.
    /// </summary>
    /// <param name="context">The exchange whose response is prepared.</param>
    /// <param name="cancellationToken">A token to cancel reading the body.</param>
    /// <returns>The encoded head and the body octets.</returns>
    /// <exception cref="HttpInvalidResponseFieldException">
    /// A field name is not a token, or a value holds a control character other than HTAB.
    /// </exception>
    public static async ValueTask<(ReadOnlyMemory<byte> Head, byte[] Body)> EncodeResponseAsync(Http1Context context, CancellationToken cancellationToken)
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

        return (EncodeHead(context.Response.StatusCode, headers), bodyBytes);
    }

    /// <summary>
    /// Writes a buffered response prepared by <see cref="EncodeResponseAsync"/> and flushes it.
    /// </summary>
    /// <param name="stream">The connection stream to write to.</param>
    /// <param name="head">The encoded head.</param>
    /// <param name="body">The body octets.</param>
    /// <param name="writeBody">
    /// <see langword="false"/> for a response to <c>HEAD</c>, which carries the head a <c>GET</c> would
    /// but never a body (RFC 9110 §9.3.2).
    /// </param>
    /// <param name="cancellationToken">A token to cancel the write.</param>
    /// <returns>A task that completes when the response has been flushed.</returns>
    public static async ValueTask WriteResponseAsync(Stream stream, ReadOnlyMemory<byte> head, byte[] body, bool writeBody, CancellationToken cancellationToken)
    {
        await stream.WriteAsync(head, cancellationToken).ConfigureAwait(false);

        if (writeBody && body.Length > 0)
        {
            await stream.WriteAsync(body, cancellationToken).ConfigureAwait(false);
        }

        await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Encodes the final response head — the status line, every header field line (with RFC 6265
    /// one-line-per-cookie handling for <c>Set-Cookie</c>), and the blank line terminating the header
    /// section. Shared by the buffered response path and the incremental streaming sink
    /// (<see cref="Http1ResponseBodyStream"/>) so both commit headers identically.
    /// </summary>
    /// <param name="statusCode">The response status code.</param>
    /// <param name="headers">The response headers to emit.</param>
    /// <returns>The encoded head.</returns>
    /// <exception cref="HttpInvalidResponseFieldException">
    /// A field name is not a token, or a value holds a control character other than HTAB.
    /// </exception>
    public static ReadOnlyMemory<byte> EncodeHead(HttpStatusCode statusCode, HttpHeaderCollection headers)
    {
        // RFC 9110 §15.2 — a 1xx status is never a valid final response status. The one 1xx that ends
        // an exchange (101 Switching Protocols) is finalized out-of-band by the protocol-upgrade path,
        // whose send is suppressed, so it never reaches this shared final-head writer.
        HttpInterimResponseRules.EnsureFinalStatusCode(statusCode);

        ArrayBufferWriter<byte> head = new(InitialHeadCapacity);
        WriteStatusLine(head, statusCode);

        foreach (KeyValuePair<HttpHeaderKey, HttpHeaderValue> header in headers)
        {
            HttpResponseFieldRules.EnsureValidName(header.Key);

            // RFC 6265 §3 — Set-Cookie MUST be emitted as one field line per
            // value; combining cookies into a single comma-separated value is
            // forbidden. Every other header is comma-folded as usual.
            if (header.Key == HttpHeaderKey.SetCookie)
            {
                foreach (string? value in header.Value)
                {
                    if (!string.IsNullOrEmpty(value))
                    {
                        WriteFieldLine(head, header.Key, value);
                    }
                }
            }
            else
            {
                WriteFieldLine(head, header.Key, header.Value.Value);
            }
        }

        WriteAscii(head, LineEnd);

        return head.WrittenMemory;
    }

    private static ReadOnlyMemory<byte> EncodeInterimHead(HttpStatusCode statusCode, IHttpHeaderCollection? headers)
    {
        ArrayBufferWriter<byte> head = new(InitialHeadCapacity);
        WriteStatusLine(head, statusCode);

        if (headers is not null)
        {
            foreach (KeyValuePair<HttpHeaderKey, HttpHeaderValue> header in headers)
            {
                HttpResponseFieldRules.EnsureValidName(header.Key);

                // Emit one field line per value so a multi-valued field (e.g. several Link relations
                // in a 103 Early Hints) is expressed without comma-folding — always valid HTTP.
                foreach (string? value in header.Value)
                {
                    if (!string.IsNullOrEmpty(value))
                    {
                        WriteFieldLine(head, header.Key, value);
                    }
                }
            }
        }

        WriteAscii(head, LineEnd);

        return head.WrittenMemory;
    }

    private static void WriteStatusLine(ArrayBufferWriter<byte> head, HttpStatusCode statusCode)
    {
        WriteAscii(head, HttpVersionPrefix);
        WriteAscii(head, statusCode.ToString());
        WriteAscii(head, LineEnd);
    }

    private static void WriteFieldLine(ArrayBufferWriter<byte> head, HttpHeaderKey key, string value)
    {
        // RFC 9110 §5.5 — checked before the line is written, so a refused value never reaches the buffer.
        HttpResponseFieldRules.EnsureValidValue(key, value);

        WriteAscii(head, key.Value);
        WriteAscii(head, FieldSeparator);
        WriteAscii(head, value);
        WriteAscii(head, LineEnd);
    }

    private static void WriteAscii(ArrayBufferWriter<byte> head, ReadOnlySpan<char> text)
    {
        // ASCII is one octet per character; a character outside it is written as '?'.
        int written = Encoding.ASCII.GetBytes(text, head.GetSpan(text.Length));
        head.Advance(written);
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
        byte[] buffer = Encoding.ASCII.GetBytes(value);
        return new ValueTask(stream.WriteAsync(buffer, 0, buffer.Length, cancellationToken));
    }
}

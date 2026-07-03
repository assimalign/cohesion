using System;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

using Assimalign.Cohesion.Http.Connections.Internal;

namespace Assimalign.Cohesion.Http.Connections.Internal.Http1;

internal static class Http1MessageReader
{
    /// <summary>
    /// Reads a single HTTP/1.1 request from the wire, enforcing the configured server limits and
    /// driving the read-timeout phase transitions.
    /// </summary>
    /// <param name="stream">The connection stream to read from.</param>
    /// <param name="connectionInfo">The transport endpoints for the exchange.</param>
    /// <param name="scheme">The connection scheme (derived from the transport's security).</param>
    /// <param name="limits">The server limits enforced on the request line, headers, and body.</param>
    /// <param name="readTimeout">
    /// The read-timeout controller. Signalled when the request line begins and once the header
    /// section has been fully received; its token bounds every read.
    /// </param>
    /// <param name="connectionToken">
    /// The ambient connection token used as the request's abort token. Distinct from
    /// <paramref name="readTimeout"/>'s token, which is disposed when the read completes.
    /// </param>
    /// <returns>The parsed request context, or <see langword="null"/> on a clean end-of-stream.</returns>
    /// <exception cref="Http1LimitExceededException">
    /// Thrown when the request violates a configured limit (414 / 431 / 413).
    /// </exception>
    public static async ValueTask<Http1Context?> ReadRequestAsync(
        Stream stream,
        HttpConnectionInfo connectionInfo,
        HttpScheme scheme,
        HttpServerLimits limits,
        Http1ReadTimeout readTimeout,
        CancellationToken connectionToken)
    {
        CancellationToken readToken = readTimeout.Token;

        // RFC 9112 §3 — the request line is size-bounded; an over-long line is 414 (RFC 9110
        // §15.5.15). Signal the timeout controller on the first byte so the keep-alive idle window
        // tightens to the request-headers deadline (Slowloris defence).
        string? requestLine = await ReadLineAsync(
            stream,
            limits.MaxRequestLineSize,
            HttpStatusCode.RequestUriTooLong,
            "request line",
            readTimeout.OnRequestLineStarted,
            readToken).ConfigureAwait(false);

        if (requestLine is null)
        {
            return null;
        }

        if (string.IsNullOrWhiteSpace(requestLine))
        {
            return null;
        }

        string[] requestLineParts = requestLine.Split(' ', 3, StringSplitOptions.RemoveEmptyEntries);

        if (requestLineParts.Length != 3)
        {
            throw new InvalidDataException($"The HTTP/1.1 request line '{requestLine}' is invalid.");
        }

        if (!string.Equals(requestLineParts[2], "HTTP/1.1", StringComparison.Ordinal))
        {
            throw new InvalidDataException($"The HTTP version '{requestLineParts[2]}' is not supported by the HTTP/1.1 transport.");
        }

        HttpMethod method = HttpMethod.GetCanonicalizedValue(requestLineParts[0]);
        // RFC 9112 §3.2 — parse the request-target into one of the four canonical forms
        // (origin / absolute / authority / asterisk) with method/form pairing enforcement.
        if (!HttpRequestTarget.TryParse(requestLineParts[1], method, out HttpRequestTarget target, out string? targetError))
        {
            throw new InvalidDataException(
                $"The HTTP/1.1 request line '{requestLine}' has a malformed request-target: {targetError}");
        }

        HttpHeaderCollection headers = await ReadHeadersAsync(stream, limits, readToken).ConfigureAwait(false);

        // The header section is fully received; disarm the request-headers deadline so the body
        // read is not bounded by it (body-read data-rate limits are deferred behind the
        // streaming-body rework).
        readTimeout.OnHeadReceived();

        // RFC 9110 §9.3.6 — CONNECT requests MUST NOT consume octets past the request
        // headers; the bytes that follow belong to the tunnel. Detect this before any
        // body framing logic kicks in. We only care about the framing decision here;
        // exposing a typed protocol-upgrade feature is the Assimalign.Cohesion.Http.ProtocolUpgrade
        // package's job and requires a separate transport <-> ProtocolUpgrade bridge
        // (tracked as follow-up).
        bool isConnectTunnel = method == HttpMethod.Connect && target.Form == HttpRequestTargetForm.Authority;

        // The effective per-request body-size cap starts at the connection-wide default and is
        // surfaced through a typed feature (below) so endpoints/middleware can observe it.
        HttpMaxRequestBodySizeFeature maxBodySizeFeature = new(limits.MaxRequestBodySize);

        byte[] bodyBytes;
        HttpTrailerCollection? requestTrailers = null;
        if (isConnectTunnel)
        {
            // RFC 9110 §9.3.6 — a CONNECT request body is not framed by Content-Length
            // or Transfer-Encoding. Anything after the headers is tunnel traffic.
            bodyBytes = Array.Empty<byte>();
        }
        else
        {
            // RFC 9112 §6 / §7 — read the body using the framing rules signalled by the
            // headers, rejecting ambiguous combinations and malformed Content-Length /
            // chunked encodings, and enforcing the effective body-size cap (413).
            Http1MessageBody messageBody = await Http1MessageBodyReader.ReadAsync(
                stream,
                headers,
                maxBodySizeFeature.MaxRequestBodySize,
                readToken).ConfigureAwait(false);
            bodyBytes = messageBody.Body;
            // RFC 9112 §7.1.2 — only chunked transfer can carry a trailer
            // section. Surface the parsed trailers (possibly empty) as a
            // supported trailer collection on the request; non-chunked requests
            // cannot carry trailers and keep the default unsupported collection.
            if (HeaderContainsToken(headers, HttpHeaderKey.TransferEncoding, "chunked"))
            {
                requestTrailers = new HttpTrailerCollection(messageBody.Trailers, isSupported: true);
            }
        }

        // The body has been read; the effective cap is now fixed for the exchange.
        maxBodySizeFeature.MakeReadOnly();

        HttpQueryCollection queryCollection = new HttpQuery(target.Query.Value).Parse();

        // Host resolution depends on the request-target form (RFC 9112 §3.2.2 / §3.2.3):
        //   - absolute-form  → authority component of the target supersedes any Host header
        //   - authority-form → the target itself IS the authority (CONNECT)
        //   - origin-form / asterisk → fall back to the Host header
        HttpHost host = target.Form switch
        {
            HttpRequestTargetForm.Absolute => target.Host,
            HttpRequestTargetForm.Authority => target.Host,
            _ => headers.TryGetValue(HttpHeaderKey.Host, out HttpHeaderValue hostValue)
                ? new HttpHost(hostValue.Value)
                : HttpHost.Empty,
        };

        // Scheme follows the same precedence: absolute-form carries it on the wire.
        HttpScheme requestScheme = target.Form == HttpRequestTargetForm.Absolute
            ? target.Scheme
            : scheme;

        Http1Request request = new(
            host,
            target.Path,
            method,
            requestScheme,
            queryCollection,
            headers,
            new MemoryStream(bodyBytes, writable: false),
            requestTrailers);
        Http1Response response = new();

        bool keepAlive = !HeaderContainsToken(headers, HttpHeaderKey.Connection, "close");

        Http1Context context = new(
            request,
            response,
            connectionInfo,
            connectionToken,
            keepAlive);

        // Expose the effective per-request body-size cap as a typed feature so endpoints and
        // middleware can read it (and, once the streaming-body rework lands, adjust it pre-read).
        context.Features.Set<IHttpMaxRequestBodySizeFeature>(maxBodySizeFeature);

        return context;
    }

    private static async ValueTask<HttpHeaderCollection> ReadHeadersAsync(
        Stream stream,
        HttpServerLimits limits,
        CancellationToken cancellationToken)
    {
        HttpHeaderCollection headers = new();

        int headerCount = 0;
        int totalBytesRemaining = limits.MaxRequestHeadersTotalSize;

        while (true)
        {
            // The per-line cap is the remaining header budget: a single line can never push the
            // header section past its total-size bound (RFC 9110 §15.5.22 → 431).
            string? line = await ReadLineAsync(
                stream,
                totalBytesRemaining,
                HttpStatusCode.RequestHeaderFieldsTooLarge,
                "request header section",
                onFirstByte: null,
                cancellationToken).ConfigureAwait(false);

            if (line is null)
            {
                throw new EndOfStreamException("The connection closed before the request headers were fully received.");
            }

            if (line.Length == 0)
            {
                return headers;
            }

            // Account this field line (and its CRLF) against the total header-section budget.
            int consumed = line.Length + 2;
            if (consumed > totalBytesRemaining)
            {
                throw new Http1LimitExceededException(
                    HttpStatusCode.RequestHeaderFieldsTooLarge,
                    $"The request header section exceeds the configured maximum size ({limits.MaxRequestHeadersTotalSize} octets).");
            }
            totalBytesRemaining -= consumed;

            headerCount++;
            if (headerCount > limits.MaxRequestHeaderCount)
            {
                throw new Http1LimitExceededException(
                    HttpStatusCode.RequestHeaderFieldsTooLarge,
                    $"The request contains more than the configured maximum of {limits.MaxRequestHeaderCount} header fields.");
            }

            int separatorIndex = line.IndexOf(':');

            if (separatorIndex <= 0)
            {
                throw new InvalidDataException($"The HTTP header '{line}' is invalid.");
            }

            string name = line[..separatorIndex].Trim();
            string value = line[(separatorIndex + 1)..].Trim();
            HttpHeaderKey key = new(name);

            if (headers.TryGetValue(key, out HttpHeaderValue existingValue))
            {
                headers[key] = HttpHeaderValue.Concat(existingValue, value);
            }
            else
            {
                headers[key] = value;
            }
        }
    }

    private static bool HeaderContainsToken(HttpHeaderCollection headers, HttpHeaderKey key, string expected)
    {
        if (!headers.TryGetValue(key, out HttpHeaderValue value))
        {
            return false;
        }

        foreach (string? entry in value)
        {
            if (entry is null)
            {
                continue;
            }

            string[] segments = entry.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

            foreach (string segment in segments)
            {
                if (string.Equals(segment, expected, StringComparison.OrdinalIgnoreCase))
                {
                    return true;
                }
            }
        }

        return false;
    }

    /// <summary>
    /// Reads a single CRLF-terminated line, capping its length so an unbounded line cannot be
    /// buffered into an ever-growing <see cref="MemoryStream"/> (a live memory-exhaustion vector).
    /// </summary>
    /// <param name="stream">The connection stream.</param>
    /// <param name="maxLineSize">The maximum number of payload octets the line may contain.</param>
    /// <param name="overflowStatus">The HTTP status to reject with when the cap is exceeded.</param>
    /// <param name="subject">A short description of the line for the rejection message.</param>
    /// <param name="onFirstByte">
    /// Invoked once when the first byte of the line is read; used to signal the read-timeout phase
    /// transition. May be <see langword="null"/>.
    /// </param>
    /// <param name="cancellationToken">A token to cancel the read.</param>
    /// <returns>The decoded line, or <see langword="null"/> on a clean end-of-stream before any byte.</returns>
    /// <exception cref="Http1LimitExceededException">Thrown when the line exceeds <paramref name="maxLineSize"/>.</exception>
    private static async ValueTask<string?> ReadLineAsync(
        Stream stream,
        int maxLineSize,
        HttpStatusCode overflowStatus,
        string subject,
        Action? onFirstByte,
        CancellationToken cancellationToken)
    {
        using MemoryStream buffer = new();
        bool sawCarriageReturn = false;
        bool signaledFirstByte = false;

        while (true)
        {
            int value = await ReadByteAsync(stream, cancellationToken).ConfigureAwait(false);

            if (value < 0)
            {
                if (buffer.Length == 0 && !sawCarriageReturn)
                {
                    return null;
                }

                throw new EndOfStreamException("The connection closed while an HTTP line was being read.");
            }

            if (!signaledFirstByte)
            {
                signaledFirstByte = true;
                onFirstByte?.Invoke();
            }

            if (sawCarriageReturn)
            {
                if (value == '\n')
                {
                    return Encoding.ASCII.GetString(buffer.GetBuffer(), 0, (int)buffer.Length);
                }

                AppendByte(buffer, (byte)'\r', maxLineSize, overflowStatus, subject);
                sawCarriageReturn = false;
            }

            if (value == '\r')
            {
                sawCarriageReturn = true;
                continue;
            }

            AppendByte(buffer, (byte)value, maxLineSize, overflowStatus, subject);
        }
    }

    private static void AppendByte(MemoryStream buffer, byte value, int maxLineSize, HttpStatusCode overflowStatus, string subject)
    {
        if (buffer.Length >= maxLineSize)
        {
            throw new Http1LimitExceededException(
                overflowStatus,
                $"The {subject} exceeds the configured maximum size ({maxLineSize} octets).");
        }

        buffer.WriteByte(value);
    }

    private static async ValueTask<int> ReadByteAsync(Stream stream, CancellationToken cancellationToken)
    {
        byte[] buffer = new byte[1];
        int bytesRead = await stream.ReadAsync(buffer.AsMemory(0, 1), cancellationToken).ConfigureAwait(false);
        return bytesRead == 0 ? -1 : buffer[0];
    }
}

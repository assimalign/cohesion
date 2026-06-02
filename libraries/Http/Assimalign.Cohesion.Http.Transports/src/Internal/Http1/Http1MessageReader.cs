using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace Assimalign.Cohesion.Http.Transports.Internal.Http1;

internal static class Http1MessageReader
{
    public static async ValueTask<Http1Context?> ReadRequestAsync(
        Stream stream,
        HttpConnectionInfo connectionInfo,
        HttpScheme scheme,
        IHttpFeatureCollection? features,
        CancellationToken cancellationToken)
    {
        string? requestLine = await ReadLineAsync(stream, cancellationToken).ConfigureAwait(false);

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

        HttpHeaderCollection headers = await ReadHeadersAsync(stream, cancellationToken).ConfigureAwait(false);

        // RFC 9110 §9.3.6 — CONNECT requests MUST NOT consume octets past the request
        // headers; the bytes that follow belong to the tunnel. Detect this before any
        // body framing logic kicks in. We only care about the framing decision here;
        // exposing a typed protocol-upgrade feature is the Assimalign.Cohesion.Http.ProtocolUpgrade
        // package's job and requires a separate transport <-> ProtocolUpgrade bridge
        // (tracked as follow-up).
        bool isConnectTunnel = method == HttpMethod.Connect && target.Form == HttpRequestTargetForm.Authority;

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
            // chunked encodings.
            Http1MessageBody messageBody = await Http1MessageBodyReader.ReadAsync(stream, headers, cancellationToken).ConfigureAwait(false);
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

        return new Http1Context(
            request,
            response,
            connectionInfo,
            cancellationToken,
            keepAlive,
            features);
    }

    private static async ValueTask<HttpHeaderCollection> ReadHeadersAsync(Stream stream, CancellationToken cancellationToken)
    {
        HttpHeaderCollection headers = new();

        while (true)
        {
            string? line = await ReadLineAsync(stream, cancellationToken).ConfigureAwait(false);

            if (line is null)
            {
                throw new EndOfStreamException("The connection closed before the request headers were fully received.");
            }

            if (line.Length == 0)
            {
                return headers;
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

    private static async ValueTask<string?> ReadLineAsync(Stream stream, CancellationToken cancellationToken)
    {
        using MemoryStream buffer = new();
        bool sawCarriageReturn = false;

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

            if (sawCarriageReturn)
            {
                if (value == '\n')
                {
                    return Encoding.ASCII.GetString(buffer.GetBuffer(), 0, (int)buffer.Length);
                }

                buffer.WriteByte((byte)'\r');
                sawCarriageReturn = false;
            }

            if (value == '\r')
            {
                sawCarriageReturn = true;
                continue;
            }

            buffer.WriteByte((byte)value);
        }
    }

    private static async ValueTask<int> ReadByteAsync(Stream stream, CancellationToken cancellationToken)
    {
        byte[] buffer = new byte[1];
        int bytesRead = await stream.ReadAsync(buffer.AsMemory(0, 1), cancellationToken).ConfigureAwait(false);
        return bytesRead == 0 ? -1 : buffer[0];
    }
}

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

        // RFC 9110 §7.8 / §9.3.6 — classify the transition signal BEFORE consuming any
        // body bytes. CONNECT in particular MUST NOT consume octets past the request
        // headers; the bytes that follow belong to the tunnel.
        (HttpProtocolUpgradeKind upgradeKind, string? upgradeProtocol) = ClassifyUpgrade(method, target, headers);

        byte[] bodyBytes;
        if (upgradeKind == HttpProtocolUpgradeKind.Connect)
        {
            // RFC 9110 §9.3.6 — a CONNECT request body is not framed by Content-Length
            // or Transfer-Encoding. Anything after the headers is tunnel traffic, so
            // we hand the application an empty body and let the upgrade hand over the
            // raw stream when AcceptAsync is invoked.
            bodyBytes = Array.Empty<byte>();
        }
        else
        {
            // RFC 9112 §6 / §7 — read the body using the framing rules signalled by the
            // headers, rejecting ambiguous combinations and malformed Content-Length /
            // chunked encodings.
            Http1MessageBody messageBody = await Http1MessageBodyReader.ReadAsync(stream, headers, cancellationToken).ConfigureAwait(false);
            bodyBytes = messageBody.Body;
            // Trailers are parsed (and validated against the smuggling-vector header list)
            // but not yet exposed on IHttpRequest — that wiring belongs to the .02
            // field-section work.
            _ = messageBody.Trailers;
        }

        HttpQueryCollection queryCollection = new HttpQuery(target.Query.Value).Parse();
        HttpCookieCollection cookies = ParseCookies(headers);

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
            cookies,
            new MemoryStream(bodyBytes, writable: false));
        Http1Response response = new();

        bool keepAlive = !HeaderContainsToken(headers, HttpHeaderKey.Connection, "close");

        return new Http1Context(
            request,
            response,
            connectionInfo,
            cancellationToken,
            keepAlive,
            stream,
            upgradeKind,
            upgradeProtocol);
    }

    /// <summary>
    /// Classifies whether the current request is a candidate for a connection
    /// transition (HTTP/1.1 protocol upgrade or CONNECT tunnel) per RFC 9110 §7.8
    /// and §9.3.6.
    /// </summary>
    /// <remarks>
    /// <para>
    /// CONNECT detection requires both the <c>CONNECT</c> method and the authority
    /// request-target form (RFC 9112 §3.2.3); the request-target parser already
    /// enforces this pairing, so the check here is defensive.
    /// </para>
    /// <para>
    /// Upgrade detection requires <c>Connection: upgrade</c> (RFC 9110 §7.8.1 —
    /// the Connection header is what makes Upgrade hop-by-hop and actionable;
    /// presence of <c>Upgrade</c> alone is informational and is ignored). The
    /// first protocol token in the <c>Upgrade</c> field is returned; further
    /// tokens describe fallback preferences and are surfaced through the raw
    /// header for the application to consult if needed.
    /// </para>
    /// </remarks>
    private static (HttpProtocolUpgradeKind Kind, string? Protocol) ClassifyUpgrade(
        HttpMethod method,
        HttpRequestTarget target,
        HttpHeaderCollection headers)
    {
        if (method == HttpMethod.Connect && target.Form == HttpRequestTargetForm.Authority)
        {
            return (HttpProtocolUpgradeKind.Connect, null);
        }

        if (!HeaderContainsToken(headers, HttpHeaderKey.Connection, "upgrade"))
        {
            return (HttpProtocolUpgradeKind.None, null);
        }

        if (!headers.TryGetValue(HttpHeaderKey.Upgrade, out HttpHeaderValue upgradeHeader))
        {
            // Connection: upgrade without an Upgrade header is a bare hop-by-hop
            // signal with no protocol to negotiate — there is nothing to switch to.
            return (HttpProtocolUpgradeKind.None, null);
        }

        // The Upgrade field is a comma-separated protocol list; the first token is
        // the highest-priority protocol the client wants to switch to.
        string raw = upgradeHeader.Value ?? string.Empty;
        int comma = raw.IndexOf(',');
        string firstProtocol = (comma < 0 ? raw : raw[..comma]).Trim();

        if (firstProtocol.Length == 0)
        {
            return (HttpProtocolUpgradeKind.None, null);
        }

        return (HttpProtocolUpgradeKind.Upgrade, firstProtocol);
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

    private static HttpCookieCollection ParseCookies(HttpHeaderCollection headers)
    {
        HttpCookieCollection cookies = new();

        if (!headers.TryGetValue(HttpHeaderKey.Cookie, out HttpHeaderValue cookieHeader))
        {
            return cookies;
        }

        foreach (string? headerValue in cookieHeader)
        {
            if (string.IsNullOrWhiteSpace(headerValue))
            {
                continue;
            }

            string[] segments = headerValue.Split(';', StringSplitOptions.RemoveEmptyEntries);

            foreach (string segment in segments)
            {
                string[] parts = segment.Split('=', 2);
                string name = parts[0].Trim();
                string value = parts.Length == 2 ? parts[1].Trim() : string.Empty;

                if (name.Length > 0)
                {
                    cookies.Add(new HttpCookie(name, value));
                }
            }
        }

        return cookies;
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

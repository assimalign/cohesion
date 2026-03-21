using System;
using System.Collections.Generic;
using System.IO;
using System.Security.Claims;
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

        HttpHeaderCollection headers = await ReadHeadersAsync(stream, cancellationToken).ConfigureAwait(false);
        byte[] bodyBytes = await ReadBodyAsync(stream, headers, cancellationToken).ConfigureAwait(false);
        HttpQueryCollection queryCollection = ParseQuery(requestLineParts[1], out HttpPath path);
        HttpCookieCollection cookies = ParseCookies(headers);
        HttpFormCollection form = ParseForm(headers, bodyBytes);
        HttpHost host = headers.TryGetValue(HttpHeaderKey.Host, out HttpHeaderValue hostValue)
            ? new HttpHost(hostValue.Value)
            : HttpHost.Empty;

        Http1Request request = new(
            host,
            path,
            HttpMethod.GetCanonicalizedValue(requestLineParts[0]),
            scheme,
            queryCollection,
            headers,
            cookies,
            form,
            new MemoryStream(bodyBytes, writable: false),
            new ClaimsPrincipal(new ClaimsIdentity()));
        Http1Response response = new();

        bool keepAlive = !HeaderContainsToken(headers, HttpHeaderKey.Connection, "close");

        return new Http1Context(request, response, connectionInfo, cancellationToken, keepAlive);
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

    private static async ValueTask<byte[]> ReadBodyAsync(Stream stream, HttpHeaderCollection headers, CancellationToken cancellationToken)
    {
        if (HeaderContainsToken(headers, HttpHeaderKey.TransferEncoding, "chunked"))
        {
            return await ReadChunkedBodyAsync(stream, cancellationToken).ConfigureAwait(false);
        }

        if (!headers.TryGetValue(HttpHeaderKey.ContentLength, out HttpHeaderValue contentLengthValue) ||
            !long.TryParse(contentLengthValue.Value, out long contentLength) ||
            contentLength <= 0)
        {
            return Array.Empty<byte>();
        }

        byte[] body = new byte[contentLength];
        int offset = 0;

        while (offset < body.Length)
        {
            int bytesRead = await stream.ReadAsync(body.AsMemory(offset, body.Length - offset), cancellationToken).ConfigureAwait(false);

            if (bytesRead == 0)
            {
                throw new EndOfStreamException("The connection closed before the request body was fully received.");
            }

            offset += bytesRead;
        }

        return body;
    }

    private static async ValueTask<byte[]> ReadChunkedBodyAsync(Stream stream, CancellationToken cancellationToken)
    {
        using MemoryStream body = new();

        while (true)
        {
            string? sizeLine = await ReadLineAsync(stream, cancellationToken).ConfigureAwait(false);

            if (sizeLine is null)
            {
                throw new EndOfStreamException("The connection closed before the chunked request body was fully received.");
            }

            string sizeText = sizeLine.Split(';', 2)[0].Trim();
            int size = Convert.ToInt32(sizeText, 16);

            if (size == 0)
            {
                await ConsumeTrailingHeadersAsync(stream, cancellationToken).ConfigureAwait(false);
                return body.ToArray();
            }

            byte[] buffer = new byte[size];
            int offset = 0;

            while (offset < size)
            {
                int bytesRead = await stream.ReadAsync(buffer.AsMemory(offset, size - offset), cancellationToken).ConfigureAwait(false);

                if (bytesRead == 0)
                {
                    throw new EndOfStreamException("The connection closed before the chunked request body was fully received.");
                }

                offset += bytesRead;
            }

            await body.WriteAsync(buffer, cancellationToken).ConfigureAwait(false);

            string? line = await ReadLineAsync(stream, cancellationToken).ConfigureAwait(false);

            if (line is null)
            {
                throw new EndOfStreamException("The connection closed before the chunk terminator was received.");
            }
        }
    }

    private static async ValueTask ConsumeTrailingHeadersAsync(Stream stream, CancellationToken cancellationToken)
    {
        while (true)
        {
            string? line = await ReadLineAsync(stream, cancellationToken).ConfigureAwait(false);

            if (line is null || line.Length == 0)
            {
                return;
            }
        }
    }

    private static HttpQueryCollection ParseQuery(string requestTarget, out HttpPath path)
    {
        if (Uri.TryCreate(requestTarget, UriKind.Absolute, out Uri? uri))
        {
            path = HttpPath.FromUriComponent(uri.AbsolutePath);
            return new HttpQuery(uri.Query).Parse();
        }

        int queryIndex = requestTarget.IndexOf('?');

        if (queryIndex >= 0)
        {
            path = HttpPath.FromUriComponent(requestTarget[..queryIndex]);
            return new HttpQuery(requestTarget[(queryIndex + 1)..]).Parse();
        }

        path = HttpPath.FromUriComponent(requestTarget);
        return new HttpQueryCollection();
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

    private static HttpFormCollection ParseForm(HttpHeaderCollection headers, byte[] bodyBytes)
    {
        HttpFormCollection form = new();

        if (bodyBytes.Length == 0 ||
            !headers.TryGetValue(HttpHeaderKey.ContentType, out HttpHeaderValue contentType) ||
            !contentType.Value.StartsWith("application/x-www-form-urlencoded", StringComparison.OrdinalIgnoreCase))
        {
            return form;
        }

        string payload = Encoding.UTF8.GetString(bodyBytes);
        HttpQueryCollection formValues = new HttpQuery(payload).Parse();

        foreach (KeyValuePair<HttpQueryKey, HttpQueryValue> pair in formValues)
        {
            form.Add(pair.Key.Value, pair.Value);
        }

        return form;
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

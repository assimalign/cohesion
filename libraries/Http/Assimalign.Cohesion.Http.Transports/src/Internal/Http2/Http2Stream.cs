using System;
using System.Collections.Generic;
using System.IO;
using System.Security.Claims;
using System.Text;
using System.Threading;

using Assimalign.Cohesion.Http.Transports.Internal.Http2.HPack;

namespace Assimalign.Cohesion.Http.Transports.Internal.Http2;

internal sealed class Http2Stream
{
    private readonly MemoryStream _headerBlock;
    private readonly MemoryStream _body;

    public Http2Stream(int streamId)
    {
        StreamId = streamId;
        _headerBlock = new MemoryStream();
        _body = new MemoryStream();
    }

    public int StreamId { get; }

    public bool HeadersCompleted { get; private set; }

    public bool InputCompleted { get; private set; }

    public bool IsRequestReady => HeadersCompleted && InputCompleted;

    public void AppendHeaders(ReadOnlySpan<byte> payload, bool endHeaders)
    {
        if (!payload.IsEmpty)
        {
            _headerBlock.Write(payload);
        }

        if (endHeaders)
        {
            HeadersCompleted = true;
        }
    }

    public void AppendBody(ReadOnlySpan<byte> payload, bool endStream)
    {
        if (!payload.IsEmpty)
        {
            _body.Write(payload);
        }

        if (endStream)
        {
            InputCompleted = true;
        }
    }

    public void CompleteInput()
    {
        InputCompleted = true;
    }

    public Http2Context CreateContext(HPackDecoder decoder, IHttpConnectionInfo connectionInfo, HttpScheme fallbackScheme, CancellationToken requestAborted)
    {
        if (!IsRequestReady)
        {
            throw new InvalidOperationException("The HTTP/2 stream is not ready to create a request context.");
        }

        HPackDecodedHeaders decodedHeaders = decoder.DecodeRequestHeaders(_headerBlock.ToArray());
        byte[] bodyBytes = _body.ToArray();
        HttpQueryCollection query = ParseQuery(decodedHeaders.Path ?? "/", out HttpPath path);
        HttpCookieCollection cookies = ParseCookies(decodedHeaders.Headers);
        HttpHost host = !string.IsNullOrWhiteSpace(decodedHeaders.Authority)
            ? new HttpHost(decodedHeaders.Authority)
            : decodedHeaders.Headers.TryGetValue(HttpHeaderKey.Host, out HttpHeaderValue hostValue)
                ? new HttpHost(hostValue.Value)
                : HttpHost.Empty;
        HttpScheme scheme = decodedHeaders.Scheme is null
            ? fallbackScheme
            : string.Equals(decodedHeaders.Scheme, "https", StringComparison.OrdinalIgnoreCase) ? HttpScheme.Https : HttpScheme.Http;

        Http2Request request = new(
            host,
            path,
            HttpMethod.GetCanonicalizedValue(decodedHeaders.Method ?? HttpMethod.Get.Value),
            scheme,
            query,
            decodedHeaders.Headers,
            cookies,
            new MemoryStream(bodyBytes, writable: false),
            new ClaimsPrincipal(new ClaimsIdentity()));

        return new Http2Context(this, request, new Http2Response(), connectionInfo, requestAborted);
    }

    private static HttpQueryCollection ParseQuery(string requestTarget, out HttpPath path)
    {
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
                string[] parts = segment.Split('=', 2, StringSplitOptions.None);
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

}

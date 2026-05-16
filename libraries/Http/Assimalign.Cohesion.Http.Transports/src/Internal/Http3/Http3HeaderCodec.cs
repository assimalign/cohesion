using System;
using System.Collections.Generic;
using System.IO;
using System.Text;

namespace Assimalign.Cohesion.Http.Transports.Internal.Http3;

internal static class Http3HeaderCodec
{
    public static Http3Request DecodeRequestHeaders(ReadOnlySpan<byte> headerBlock, HttpScheme fallbackScheme, byte[] bodyBytes)
    {
        HttpHeaderCollection headers = new();
        string? authority = null;
        string? method = null;
        string? pathValue = null;
        string? schemeValue = null;
        int index = 0;

        QuicVariableLengthInteger.Decode(headerBlock, ref index);
        QuicVariableLengthInteger.Decode(headerBlock, ref index);

        while (index < headerBlock.Length)
        {
            byte current = headerBlock[index];

            if ((current & 0xF0) != 0x30)
            {
                throw new NotSupportedException("Only literal QPACK header fields without name references are supported by the rebuilt HTTP/3 transport.");
            }

            string name = DecodeString(headerBlock, ref index, 3);
            string value = DecodeString(headerBlock, ref index, 7);

            if (name[0] == ':')
            {
                switch (name)
                {
                    case ":authority":
                        authority = value;
                        break;
                    case ":method":
                        method = value;
                        break;
                    case ":path":
                        pathValue = value;
                        break;
                    case ":scheme":
                        schemeValue = value;
                        break;
                }

                continue;
            }

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

        HttpQueryCollection query = ParseQuery(pathValue ?? "/", out HttpPath path);
        HttpCookieCollection cookies = ParseCookies(headers);
        HttpHost host = !string.IsNullOrWhiteSpace(authority)
            ? new HttpHost(authority)
            : headers.TryGetValue(HttpHeaderKey.Host, out HttpHeaderValue hostValue)
                ? new HttpHost(hostValue.Value)
                : HttpHost.Empty;
        HttpScheme scheme = schemeValue is null
            ? fallbackScheme
            : string.Equals(schemeValue, "https", StringComparison.OrdinalIgnoreCase) ? HttpScheme.Https : HttpScheme.Http;

        return new Http3Request(
            host,
            path,
            HttpMethod.GetCanonicalizedValue(method ?? HttpMethod.Get.Value),
            scheme,
            query,
            headers,
            cookies,
            new MemoryStream(bodyBytes, writable: false));
    }

    public static byte[] EncodeResponseHeaders(Http3Context context, byte[] bodyBytes)
    {
        HttpHeaderCollection headers = context.Response.Headers;

        if (!headers.ContainsKey(HttpHeaderKey.ContentLength))
        {
            headers[HttpHeaderKey.ContentLength] = bodyBytes.Length.ToString(System.Globalization.CultureInfo.InvariantCulture);
        }

        using MemoryStream buffer = new();
        QuicVariableLengthInteger.Write(buffer, 0);
        QuicVariableLengthInteger.Write(buffer, 0);
        WriteLiteralHeader(buffer, ":status", ((int)context.Response.StatusCode).ToString(System.Globalization.CultureInfo.InvariantCulture));

        foreach (KeyValuePair<HttpHeaderKey, HttpHeaderValue> header in headers)
        {
            WriteLiteralHeader(buffer, header.Key.Value.ToLowerInvariant(), header.Value.Value);
        }

        foreach (HttpCookie cookie in context.Response.Cookies)
        {
            WriteLiteralHeader(buffer, "set-cookie", cookie.ToString());
        }

        return buffer.ToArray();
    }

    private static string DecodeString(ReadOnlySpan<byte> buffer, ref int index, int prefixLength)
    {
        byte first = buffer[index];

        if (((first >> prefixLength) & 0x1) != 0)
        {
            throw new NotSupportedException("Huffman encoded QPACK strings are not yet supported by the rebuilt HTTP/3 transport.");
        }

        int length = DecodePrefixedInteger(buffer, ref index, prefixLength);

        if (index + length > buffer.Length)
        {
            throw new InvalidDataException("The QPACK string literal is incomplete.");
        }

        string value = Encoding.ASCII.GetString(buffer.Slice(index, length));
        index += length;

        return value;
    }

    private static int DecodePrefixedInteger(ReadOnlySpan<byte> buffer, ref int index, int prefixLength)
    {
        byte first = buffer[index++];
        int mask = (1 << prefixLength) - 1;
        int value = first & mask;

        if (value < mask)
        {
            return value;
        }

        int shift = 0;

        while (index < buffer.Length)
        {
            byte next = buffer[index++];
            value += (next & 0x7F) << shift;

            if ((next & 0x80) == 0)
            {
                return value;
            }

            shift += 7;
        }

        throw new InvalidDataException("The QPACK integer is incomplete.");
    }

    private static void WriteLiteralHeader(Stream stream, string name, string value)
    {
        WritePrefixedInteger(stream, name.Length, 3, 0x30);
        byte[] nameBytes = Encoding.ASCII.GetBytes(name);
        stream.Write(nameBytes, 0, nameBytes.Length);
        WritePrefixedInteger(stream, value.Length, 7, 0x00);
        byte[] valueBytes = Encoding.ASCII.GetBytes(value);
        stream.Write(valueBytes, 0, valueBytes.Length);
    }

    private static void WritePrefixedInteger(Stream stream, int value, int prefixLength, byte prefixMask)
    {
        int maxPrefixValue = (1 << prefixLength) - 1;

        if (value < maxPrefixValue)
        {
            stream.WriteByte((byte)(prefixMask | value));
            return;
        }

        stream.WriteByte((byte)(prefixMask | maxPrefixValue));
        value -= maxPrefixValue;

        while (value >= 128)
        {
            stream.WriteByte((byte)((value % 128) + 128));
            value /= 128;
        }

        stream.WriteByte((byte)value);
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

}

using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;

using Assimalign.Cohesion.Http.Internal;

namespace Assimalign.Cohesion.Http.Connections.Internal.Http2.HPack;

internal static partial class HPackEncoder
{
    public static byte[] EncodeResponseHeaders(HttpStatusCode statusCode, IHttpHeaderCollection headers, int bodyLength)
    {
        if (!headers.ContainsKey(HttpHeaderKey.ContentLength))
        {
            headers[HttpHeaderKey.ContentLength] = bodyLength.ToString(CultureInfo.InvariantCulture);
        }

        return EncodeResponseHeaders(statusCode, headers);
    }

    /// <summary>
    /// Encodes the response field section for an <em>incrementally streamed</em>
    /// response: the <c>:status</c> pseudo-header followed by the supplied headers
    /// verbatim, with <b>no</b> <c>Content-Length</c> synthesized. A streaming
    /// response has no known body length up front — HTTP/2 delimits the body with
    /// <c>END_STREAM</c> — so injecting a length here would be wrong.
    /// </summary>
    /// <param name="statusCode">The response status code.</param>
    /// <param name="headers">The response headers to emit as-is.</param>
    /// <returns>The HPACK-encoded field section.</returns>
    public static byte[] EncodeResponseHeaders(HttpStatusCode statusCode, IHttpHeaderCollection headers)
    {
        using MemoryStream buffer = new();
        WriteStatusHeader(buffer, (int)statusCode);

        foreach (KeyValuePair<HttpHeaderKey, HttpHeaderValue> header in headers)
        {
            // RFC 6265 §3 — Set-Cookie MUST be emitted as one field line per
            // value; combining cookies into a single comma-folded value is
            // forbidden.
            if (header.Key == HttpHeaderKey.SetCookie)
            {
                foreach (string? value in header.Value)
                {
                    if (!string.IsNullOrEmpty(value))
                    {
                        WriteHeader(buffer, "set-cookie", value);
                    }
                }
            }
            else
            {
                WriteHeader(buffer, header.Key.Value.ToLowerInvariant(), header.Value.Value);
            }
        }

        return buffer.ToArray();
    }

    public static bool EncodeIndexedHeaderField(int index, Span<byte> destination, out int bytesWritten)
    {
        if (destination.IsEmpty)
        {
            bytesWritten = 0;
            return false;
        }

        destination[0] = 0x80;
        return IntegerEncoder.Encode(index, 7, destination, out bytesWritten);
    }

    public static bool EncodeStatusHeader(int statusCode, Span<byte> destination, out int bytesWritten)
    {
        if (HPackStaticTable.TryGetStatusIndex(statusCode, out int index))
        {
            return EncodeIndexedHeaderField(index, destination, out bytesWritten);
        }

        if (!EncodeLiteralHeaderFieldWithoutIndexing(HPackStaticTable.Status200, destination, out int nameLength))
        {
            bytesWritten = 0;
            return false;
        }

        if (!EncodeStringLiteral(statusCode.ToString(CultureInfo.InvariantCulture), destination.Slice(nameLength), out int valueLength))
        {
            bytesWritten = 0;
            return false;
        }

        bytesWritten = nameLength + valueLength;
        return true;
    }

    public static bool EncodeLiteralHeaderFieldWithoutIndexing(int index, string value, Encoding? valueEncoding, Span<byte> destination, out int bytesWritten)
    {
        if (destination.Length < 2)
        {
            bytesWritten = 0;
            return false;
        }

        destination[0] = 0;

        if (!IntegerEncoder.Encode(index, 4, destination, out int indexLength) ||
            !EncodeStringLiteral(value, valueEncoding, destination.Slice(indexLength), out int valueLength))
        {
            bytesWritten = 0;
            return false;
        }

        bytesWritten = indexLength + valueLength;
        return true;
    }

    public static bool EncodeLiteralHeaderFieldWithoutIndexing(int index, Span<byte> destination, out int bytesWritten)
    {
        if (destination.IsEmpty)
        {
            bytesWritten = 0;
            return false;
        }

        destination[0] = 0;
        return IntegerEncoder.Encode(index, 4, destination, out bytesWritten);
    }

    public static bool EncodeLiteralHeaderFieldWithoutIndexingNewName(string name, string value, Encoding? valueEncoding, Span<byte> destination, out int bytesWritten)
    {
        if (destination.Length < 3)
        {
            bytesWritten = 0;
            return false;
        }

        destination[0] = 0;

        if (!EncodeLiteralHeaderName(name, destination.Slice(1), out int nameLength) ||
            !EncodeStringLiteral(value, valueEncoding, destination.Slice(1 + nameLength), out int valueLength))
        {
            bytesWritten = 0;
            return false;
        }

        bytesWritten = 1 + nameLength + valueLength;
        return true;
    }

    public static bool EncodeLiteralHeaderFieldWithoutIndexingNewName(string name, Span<byte> destination, out int bytesWritten)
    {
        if (destination.Length < 2)
        {
            bytesWritten = 0;
            return false;
        }

        destination[0] = 0;

        if (!EncodeLiteralHeaderName(name, destination.Slice(1), out int nameLength))
        {
            bytesWritten = 0;
            return false;
        }

        bytesWritten = 1 + nameLength;
        return true;
    }

    public static bool EncodeStringLiteral(ReadOnlySpan<byte> value, Span<byte> destination, out int bytesWritten)
    {
        if (destination.IsEmpty)
        {
            bytesWritten = 0;
            return false;
        }

        destination[0] = 0;

        if (!IntegerEncoder.Encode(value.Length, 7, destination, out int integerLength))
        {
            bytesWritten = 0;
            return false;
        }

        destination = destination.Slice(integerLength);

        if (value.Length > destination.Length)
        {
            bytesWritten = 0;
            return false;
        }

        value.CopyTo(destination);
        bytesWritten = integerLength + value.Length;
        return true;
    }

    public static bool EncodeStringLiteral(string value, Span<byte> destination, out int bytesWritten)
    {
        return EncodeStringLiteral(value, valueEncoding: null, destination, out bytesWritten);
    }

    public static bool EncodeStringLiteral(string value, Encoding? valueEncoding, Span<byte> destination, out int bytesWritten)
    {
        if (destination.IsEmpty)
        {
            bytesWritten = 0;
            return false;
        }

        destination[0] = 0;
        int encodedStringLength = valueEncoding is null || ReferenceEquals(valueEncoding, Encoding.Latin1)
            ? value.Length
            : valueEncoding.GetByteCount(value);

        if (!IntegerEncoder.Encode(encodedStringLength, 7, destination, out int integerLength))
        {
            bytesWritten = 0;
            return false;
        }

        destination = destination.Slice(integerLength);

        if (encodedStringLength > destination.Length)
        {
            bytesWritten = 0;
            return false;
        }

        if (valueEncoding is null)
        {
            EncodeValueStringPart(value, destination);
        }
        else
        {
            valueEncoding.GetBytes(value, destination);
        }

        bytesWritten = integerLength + encodedStringLength;
        return true;
    }

    private static bool EncodeLiteralHeaderName(string value, Span<byte> destination, out int bytesWritten)
    {
        if (destination.IsEmpty)
        {
            bytesWritten = 0;
            return false;
        }

        destination[0] = 0;

        if (!IntegerEncoder.Encode(value.Length, 7, destination, out int integerLength))
        {
            bytesWritten = 0;
            return false;
        }

        destination = destination.Slice(integerLength);

        if (value.Length > destination.Length)
        {
            bytesWritten = 0;
            return false;
        }

        for (int index = 0; index < value.Length; index++)
        {
            char character = value[index];
            destination[index] = (byte)((uint)(character - 'A') <= ('Z' - 'A') ? character | 0x20 : character);
        }

        bytesWritten = integerLength + value.Length;
        return true;
    }

    private static void EncodeValueStringPart(string value, Span<byte> destination)
    {
        for (int index = 0; index < value.Length; index++)
        {
            char character = value[index];

            if ((character & 0xFF80) != 0)
            {
                throw new InvalidOperationException("Only ASCII HPACK literals are currently supported.");
            }

            destination[index] = (byte)character;
        }
    }

    private static void WriteStatusHeader(Stream stream, int statusCode)
    {
        byte[] buffer = new byte[32];

        while (!EncodeStatusHeader(statusCode, buffer, out int bytesWritten))
        {
            buffer = new byte[buffer.Length * 2];
        }

        EncodeStatusHeader(statusCode, buffer, out int written);
        stream.Write(buffer, 0, written);
    }

    private static void WriteHeader(Stream stream, string name, string value)
    {
        byte[] buffer = new byte[128];

        while (!TryEncodeHeader(name, value, buffer, out int bytesWritten))
        {
            buffer = new byte[buffer.Length * 2];
        }

        TryEncodeHeader(name, value, buffer, out int written);
        stream.Write(buffer, 0, written);
    }

    private static bool TryEncodeHeader(string name, string value, Span<byte> destination, out int bytesWritten)
    {
        if (HPackStaticTable.TryGetNameIndex(name, out int index))
        {
            return EncodeLiteralHeaderFieldWithoutIndexing(index, value, Encoding.ASCII, destination, out bytesWritten);
        }

        return EncodeLiteralHeaderFieldWithoutIndexingNewName(name, value, Encoding.ASCII, destination, out bytesWritten);
    }
}

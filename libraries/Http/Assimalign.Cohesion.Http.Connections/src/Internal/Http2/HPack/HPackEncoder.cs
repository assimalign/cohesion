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

        byte[] octets;

        if (valueEncoding is null || ReferenceEquals(valueEncoding, Encoding.Latin1))
        {
            octets = new byte[value.Length];

            for (int index = 0; index < value.Length; index++)
            {
                char character = value[index];

                // The null-encoding path preserves the historical ASCII-only
                // guard; Latin1 accepts the full 0-255 octet range.
                if (valueEncoding is null && (character & 0xFF80) != 0)
                {
                    throw new InvalidOperationException("Only ASCII HPACK literals are currently supported.");
                }

                octets[index] = (byte)character;
            }
        }
        else
        {
            octets = valueEncoding.GetBytes(value);
        }

        return EncodeStringLiteralShortest(octets, destination, out bytesWritten);
    }

    private static bool EncodeLiteralHeaderName(string value, Span<byte> destination, out int bytesWritten)
    {
        byte[] octets = new byte[value.Length];

        for (int index = 0; index < value.Length; index++)
        {
            char character = value[index];
            octets[index] = (byte)((uint)(character - 'A') <= ('Z' - 'A') ? character | 0x20 : character);
        }

        return EncodeStringLiteralShortest(octets, destination, out bytesWritten);
    }

    /// <summary>
    /// Writes an HPACK string literal (RFC 7541 §5.2) choosing the shorter of
    /// the raw and Huffman (RFC 7541 Appendix B) forms. The Huffman flag (H) is
    /// set only when the Huffman encoding is strictly shorter than the raw
    /// octets; both forms decode through <see cref="HPackHuffmanDecoder"/>.
    /// </summary>
    private static bool EncodeStringLiteralShortest(ReadOnlySpan<byte> octets, Span<byte> destination, out int bytesWritten)
    {
        if (destination.IsEmpty)
        {
            bytesWritten = 0;
            return false;
        }

        int huffmanLength = HPackHuffmanEncoder.GetEncodedLength(octets);

        if (huffmanLength < octets.Length)
        {
            destination[0] = 0x80; // H = 1

            if (!IntegerEncoder.Encode(huffmanLength, 7, destination, out int prefixLength))
            {
                bytesWritten = 0;
                return false;
            }

            Span<byte> tail = destination.Slice(prefixLength);

            if (huffmanLength > tail.Length)
            {
                bytesWritten = 0;
                return false;
            }

            HPackHuffmanEncoder.Encode(octets, tail);
            bytesWritten = prefixLength + huffmanLength;
            return true;
        }

        destination[0] = 0; // H = 0

        if (!IntegerEncoder.Encode(octets.Length, 7, destination, out int rawPrefixLength))
        {
            bytesWritten = 0;
            return false;
        }

        Span<byte> rawTail = destination.Slice(rawPrefixLength);

        if (octets.Length > rawTail.Length)
        {
            bytesWritten = 0;
            return false;
        }

        octets.CopyTo(rawTail);
        bytesWritten = rawPrefixLength + octets.Length;
        return true;
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

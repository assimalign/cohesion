using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;

using Assimalign.Cohesion.Http.Internal;

namespace Assimalign.Cohesion.Http.Connections.Internal;

internal static partial class HPackEncoder
{
    /// <summary>
    /// Encodes the response field section for a <em>buffered</em> response: the section
    /// <see cref="EncodeResponseHeaders(HttpStatusCode, IHttpHeaderCollection)"/> encodes, with a
    /// <c>content-length</c> synthesized from <paramref name="bodyLength"/> when the application set none.
    /// The synthesized field is added to <paramref name="headers"/>.
    /// </summary>
    /// <param name="statusCode">The response status code.</param>
    /// <param name="headers">The response headers to emit.</param>
    /// <param name="bodyLength">The length of the buffered body.</param>
    /// <returns>The HPACK-encoded field section.</returns>
    /// <exception cref="HttpInvalidResponseFieldException">
    /// A field name is not a token, or a value holds a control character other than HTAB. The synthesized
    /// <c>content-length</c> is removed again first, so a response sent in this one's place is framed by its
    /// own body.
    /// </exception>
    public static byte[] EncodeResponseHeaders(HttpStatusCode statusCode, IHttpHeaderCollection headers, int bodyLength)
    {
        if (headers.ContainsKey(HttpHeaderKey.ContentLength))
        {
            return EncodeResponseHeaders(statusCode, headers);
        }

        headers[HttpHeaderKey.ContentLength] = bodyLength.ToString(CultureInfo.InvariantCulture);

        try
        {
            return EncodeResponseHeaders(statusCode, headers);
        }
        catch (HttpInvalidResponseFieldException)
        {
            // A refused head leaves the response replaceable (#1183); a stale length would make the
            // replacement malformed (RFC 9113 §8.1.1) whenever its body differs.
            headers.Remove(HttpHeaderKey.ContentLength);
            throw;
        }
    }

    /// <summary>
    /// Encodes the response field section for an <em>incrementally streamed</em>
    /// response: the <c>:status</c> pseudo-header followed by the supplied headers,
    /// with <b>no</b> <c>Content-Length</c> synthesized. A streaming
    /// response has no known body length up front — HTTP/2 delimits the body with
    /// <c>END_STREAM</c> — so injecting a length here would be wrong. A
    /// connection-specific field is skipped (<see cref="HttpResponseFieldRules"/>).
    /// </summary>
    /// <remarks>
    /// Every field is checked against the field syntax before the section is returned
    /// (<see cref="HttpResponseFieldRules.EnsureValidName"/>, <see cref="HttpResponseFieldRules.EnsureValidValue"/>),
    /// the connection-specific ones included, so a field that would split an HTTP/1.1 head is refused
    /// on every version alike. The section is built in memory and the encoder never indexes, so a refusal
    /// leaves nothing on the wire and no HPACK state behind.
    /// </remarks>
    /// <param name="statusCode">The response status code.</param>
    /// <param name="headers">The response headers to emit.</param>
    /// <returns>The HPACK-encoded field section.</returns>
    /// <exception cref="HttpInvalidResponseFieldException">A field name is not a token, or a value holds a control character other than HTAB.</exception>
    public static byte[] EncodeResponseHeaders(HttpStatusCode statusCode, IHttpHeaderCollection headers)
    {
        using MemoryStream buffer = new();
        WriteStatusHeader(buffer, (int)statusCode);

        foreach (KeyValuePair<HttpHeaderKey, HttpHeaderValue> header in headers)
        {
            HttpResponseFieldRules.EnsureValidName(header.Key);

            // RFC 6265 §3 — Set-Cookie MUST be emitted as one field line per
            // value; combining cookies into a single comma-folded value is
            // forbidden.
            if (header.Key == HttpHeaderKey.SetCookie)
            {
                foreach (string? value in header.Value)
                {
                    if (!string.IsNullOrEmpty(value))
                    {
                        HttpResponseFieldRules.EnsureValidValue(header.Key, value);
                        WriteHeader(buffer, "set-cookie", value);
                    }
                }

                continue;
            }

            string fieldValue = header.Value.Value;
            HttpResponseFieldRules.EnsureValidValue(header.Key, fieldValue);

            // RFC 9113 §8.2.2 — a connection-specific field would make the response malformed.
            if (HttpResponseFieldRules.IsSendable(header.Key, header.Value))
            {
                WriteHeader(buffer, header.Key.Value.ToLowerInvariant(), fieldValue);
            }
        }

        return buffer.ToArray();
    }

    /// <summary>
    /// Encodes a response's trailer section (RFC 9113 §8.1): the staged fields, lowercased, with no
    /// pseudo-header field — a trailer section carries none.
    /// </summary>
    /// <param name="trailers">
    /// The staged trailer fields, already checked when they were added. Each is checked again against the
    /// field syntax here, because a value built over an array can change after it was staged.
    /// </param>
    /// <returns>The HPACK-encoded field block.</returns>
    /// <exception cref="HttpInvalidResponseFieldException">A field name is not a token, or a value holds a control character other than HTAB.</exception>
    public static byte[] EncodeTrailers(IHttpHeaderCollection trailers)
    {
        using MemoryStream buffer = new();

        foreach (KeyValuePair<HttpHeaderKey, HttpHeaderValue> field in trailers)
        {
            string fieldValue = field.Value.Value;
            HttpResponseFieldRules.EnsureValidName(field.Key);
            HttpResponseFieldRules.EnsureValidValue(field.Key, fieldValue);
            WriteHeader(buffer, field.Key.Value.ToLowerInvariant(), fieldValue);
        }

        return buffer.ToArray();
    }

    /// <summary>
    /// Encodes the field section for an <em>interim</em> (<c>1xx</c>) response: the <c>:status</c>
    /// pseudo-header set to the interim code followed by the supplied fields, less any
    /// connection-specific field (<see cref="HttpResponseFieldRules"/>), with <b>no</b>
    /// <c>Content-Length</c> (an interim response carries no body — RFC 9110 §15.2). The resulting
    /// HEADERS frame is written without <c>END_STREAM</c> so the final response can follow on the same
    /// stream (RFC 9113 §8.1).
    /// </summary>
    /// <param name="statusCode">The interim status code (validated by the caller to be 1xx, not 101).</param>
    /// <param name="headers">The interim response fields, or <see langword="null"/> for none.</param>
    /// <returns>The HPACK-encoded field section.</returns>
    /// <exception cref="HttpInvalidResponseFieldException">A field name is not a token, or a value holds a control character other than HTAB.</exception>
    public static byte[] EncodeInterimResponseHeaders(HttpStatusCode statusCode, IHttpHeaderCollection? headers)
    {
        using MemoryStream buffer = new();
        WriteStatusHeader(buffer, (int)statusCode);

        if (headers is not null)
        {
            foreach (KeyValuePair<HttpHeaderKey, HttpHeaderValue> header in headers)
            {
                HttpResponseFieldRules.EnsureValidName(header.Key);

                // RFC 9113 §8.2.2 — an interim response is a field section like any other.
                bool sendable = HttpResponseFieldRules.IsSendable(header.Key, header.Value);

                foreach (string? value in header.Value)
                {
                    if (!string.IsNullOrEmpty(value))
                    {
                        HttpResponseFieldRules.EnsureValidValue(header.Key, value);

                        if (sendable)
                        {
                            WriteHeader(buffer, header.Key.Value.ToLowerInvariant(), value);
                        }
                    }
                }
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

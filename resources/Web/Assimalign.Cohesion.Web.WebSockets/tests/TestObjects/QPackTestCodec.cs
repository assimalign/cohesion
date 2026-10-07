using System;
using System.Collections.Generic;
using System.IO;
using System.Text;

namespace Assimalign.Cohesion.Web.WebSockets.Tests.TestObjects;

/// <summary>
/// The client half of QPACK (RFC 9204) and HTTP/3 framing (RFC 9114 §7.1) that a test needs to speak
/// to the server over the in-memory driver: request field sections as literal field lines, and a
/// decoder for the representations the server's static-only encoder emits (indexed and name-reference
/// static lines, literal names, Huffman-coded strings).
/// </summary>
/// <remarks>
/// The Huffman decoder covers the codes of 5 to 15 bits (RFC 7541 Appendix B): every printable ASCII
/// character except <c>\</c>, plus NUL. Header names and values outside that set fail the decode with
/// an exception rather than decoding wrongly.
/// </remarks>
internal static class QPackTestCodec
{
    /// <summary>The HTTP/3 DATA frame type (RFC 9114 §7.2.1).</summary>
    public const long DataFrame = 0x0;

    /// <summary>The HTTP/3 HEADERS frame type (RFC 9114 §7.2.2).</summary>
    public const long HeadersFrame = 0x1;

    // RFC 9204 Appendix A.
    private static readonly (string Name, string Value)[] _staticTable =
    {
        (":authority", ""), (":path", "/"), ("age", "0"), ("content-disposition", ""), ("content-length", "0"),
        ("cookie", ""), ("date", ""), ("etag", ""), ("if-modified-since", ""), ("if-none-match", ""),
        ("last-modified", ""), ("link", ""), ("location", ""), ("referer", ""), ("set-cookie", ""),
        (":method", "CONNECT"), (":method", "DELETE"), (":method", "GET"), (":method", "HEAD"), (":method", "OPTIONS"),
        (":method", "POST"), (":method", "PUT"), (":scheme", "http"), (":scheme", "https"), (":status", "103"),
        (":status", "200"), (":status", "304"), (":status", "404"), (":status", "503"), ("accept", "*/*"),
        ("accept", "application/dns-message"), ("accept-encoding", "gzip, deflate, br"), ("accept-ranges", "bytes"),
        ("access-control-allow-headers", "cache-control"), ("access-control-allow-headers", "content-type"),
        ("access-control-allow-origin", "*"), ("cache-control", "max-age=0"), ("cache-control", "max-age=2592000"),
        ("cache-control", "max-age=604800"), ("cache-control", "no-cache"), ("cache-control", "no-store"),
        ("cache-control", "public, max-age=31536000"), ("content-encoding", "br"), ("content-encoding", "gzip"),
        ("content-type", "application/dns-message"), ("content-type", "application/javascript"),
        ("content-type", "application/json"), ("content-type", "application/x-www-form-urlencoded"),
        ("content-type", "image/gif"), ("content-type", "image/jpeg"), ("content-type", "image/png"),
        ("content-type", "text/css"), ("content-type", "text/html; charset=utf-8"), ("content-type", "text/plain"),
        ("content-type", "text/plain;charset=utf-8"), ("range", "bytes=0-"), ("strict-transport-security", "max-age=31536000"),
        ("strict-transport-security", "max-age=31536000; includesubdomains"),
        ("strict-transport-security", "max-age=31536000; includesubdomains; preload"), ("vary", "accept-encoding"),
        ("vary", "origin"), ("x-content-type-options", "nosniff"), ("x-xss-protection", "1; mode=block"),
        (":status", "100"), (":status", "204"), (":status", "206"), (":status", "302"), (":status", "400"),
        (":status", "403"), (":status", "421"), (":status", "425"), (":status", "500"), ("accept-language", ""),
        ("access-control-allow-credentials", "FALSE"), ("access-control-allow-credentials", "TRUE"),
        ("access-control-allow-headers", "*"), ("access-control-allow-methods", "get"),
        ("access-control-allow-methods", "get, post, options"), ("access-control-allow-methods", "options"),
        ("access-control-expose-headers", "content-length"), ("access-control-request-headers", "content-type"),
        ("access-control-request-method", "get"), ("access-control-request-method", "post"), ("alt-svc", "clear"),
        ("authorization", ""), ("content-security-policy", "script-src 'none'; object-src 'none'; base-uri 'none'"),
        ("early-data", "1"), ("expect-ct", ""), ("forwarded", ""), ("if-range", ""), ("origin", ""),
        ("purpose", "prefetch"), ("server", ""), ("timing-allow-origin", "*"), ("upgrade-insecure-requests", "1"),
        ("user-agent", ""), ("x-forwarded-for", ""), ("x-frame-options", "deny"), ("x-frame-options", "sameorigin"),
    };

    // RFC 7541 Appendix B is a canonical Huffman code: within a length, codes ascend with the symbol,
    // so the symbols of each length, in order, are enough to rebuild every code.
    private static readonly (int Length, string Symbols)[] _huffmanLengths =
    {
        (5, "012aceiost"),
        (6, " %-./3456789=A_bdfghlmnpru"),
        (7, ":BCDEFGHIJKLMNOPQRSTUVWYjkqvwxyz"),
        (8, "&*,;XZ"),
        (10, "!\"()?"),
        (11, "'+|"),
        (12, "#>"),
        (13, "\0$@[]~"),
        (14, "^}"),
        (15, "<`{"),
    };

    private static readonly Dictionary<(int Length, int Code), char> _huffmanCodes = BuildHuffmanCodes();

    /// <summary>
    /// Encodes a HEADERS frame whose field section is <paramref name="fields"/>, in order, as literal
    /// field lines with literal names (RFC 9204 §4.5.6), without Huffman coding.
    /// </summary>
    public static byte[] EncodeHeadersFrame(IEnumerable<(string Name, string Value)> fields)
    {
        using MemoryStream section = new();
        section.WriteByte(0x00); // Required Insert Count = 0.
        section.WriteByte(0x00); // S = 0, Delta Base = 0.

        foreach ((string name, string value) in fields)
        {
            byte[] nameOctets = Encoding.ASCII.GetBytes(name);
            byte[] valueOctets = Encoding.ASCII.GetBytes(value);

            // 0 0 1 N(=0) H(=0) name length(3), then H(=0) value length(7).
            WritePrefixedInteger(section, nameOctets.Length, 3, 0b0010_0000);
            section.Write(nameOctets);
            WritePrefixedInteger(section, valueOctets.Length, 7, 0x00);
            section.Write(valueOctets);
        }

        return EncodeFrame(HeadersFrame, section.ToArray());
    }

    /// <summary>
    /// Encodes one HTTP/3 frame: its type and payload length as variable-length integers, then the payload.
    /// </summary>
    public static byte[] EncodeFrame(long type, ReadOnlySpan<byte> payload)
    {
        using MemoryStream frame = new();
        WriteVariableLengthInteger(frame, type);
        WriteVariableLengthInteger(frame, payload.Length);
        frame.Write(payload);
        return frame.ToArray();
    }

    /// <summary>
    /// Decodes a field section the server's encoder produced into its field lines, in order.
    /// </summary>
    /// <exception cref="InvalidDataException">The section uses a representation the decoder does not support.</exception>
    public static List<(string Name, string Value)> DecodeFieldSection(ReadOnlySpan<byte> section)
    {
        List<(string Name, string Value)> fields = new();
        int index = 0;

        // Field Section Prefix (§4.5.1): the server never references the dynamic table.
        if (ReadPrefixedInteger(section, ref index, 8) != 0 || ReadPrefixedInteger(section, ref index, 7) != 0)
        {
            throw new InvalidDataException("The field section references the dynamic table.");
        }

        while (index < section.Length)
        {
            byte first = section[index];

            if ((first & 0b1100_0000) == 0b1100_0000)
            {
                // §4.5.2 Indexed Field Line, static.
                fields.Add(_staticTable[ReadPrefixedInteger(section, ref index, 6)]);
            }
            else if ((first & 0b1101_0000) == 0b0101_0000)
            {
                // §4.5.4 Literal Field Line with (static) Name Reference.
                string name = _staticTable[ReadPrefixedInteger(section, ref index, 4)].Name;
                fields.Add((name, ReadString(section, ref index, 7)));
            }
            else if ((first & 0b1110_0000) == 0b0010_0000)
            {
                // §4.5.6 Literal Field Line with Literal Name.
                string name = ReadString(section, ref index, 3);
                fields.Add((name, ReadString(section, ref index, 7)));
            }
            else
            {
                throw new InvalidDataException($"Unsupported field line representation 0x{first:X2}.");
            }
        }

        return fields;
    }

    /// <summary>
    /// Reads a QUIC variable-length integer (RFC 9000 §16) at <paramref name="index"/>.
    /// </summary>
    /// <returns><see langword="false"/> when <paramref name="buffer"/> ends inside the integer.</returns>
    public static bool TryReadVariableLengthInteger(ReadOnlySpan<byte> buffer, ref int index, out long value)
    {
        value = 0;

        if (index >= buffer.Length)
        {
            return false;
        }

        int length = 1 << (buffer[index] >> 6);
        if (index + length > buffer.Length)
        {
            return false;
        }

        value = buffer[index] & 0x3F;
        for (int i = 1; i < length; i++)
        {
            value = (value << 8) | buffer[index + i];
        }

        index += length;
        return true;
    }

    private static void WriteVariableLengthInteger(Stream stream, long value)
    {
        if (value < 0x40)
        {
            stream.WriteByte((byte)value);
        }
        else if (value < 0x4000)
        {
            stream.WriteByte((byte)(0x40 | (value >> 8)));
            stream.WriteByte((byte)value);
        }
        else if (value < 0x4000_0000)
        {
            stream.WriteByte((byte)(0x80 | (value >> 24)));
            stream.WriteByte((byte)(value >> 16));
            stream.WriteByte((byte)(value >> 8));
            stream.WriteByte((byte)value);
        }
        else
        {
            for (int shift = 56; shift >= 0; shift -= 8)
            {
                byte octet = (byte)(value >> shift);
                stream.WriteByte(shift == 56 ? (byte)(0xC0 | octet) : octet);
            }
        }
    }

    // RFC 7541 §5.1, as RFC 9204 §4.1.1 reuses it.
    private static void WritePrefixedInteger(Stream stream, int value, int prefixBits, byte pattern)
    {
        int limit = (1 << prefixBits) - 1;

        if (value < limit)
        {
            stream.WriteByte((byte)(pattern | value));
            return;
        }

        stream.WriteByte((byte)(pattern | limit));
        value -= limit;

        while (value >= 0x80)
        {
            stream.WriteByte((byte)((value & 0x7F) | 0x80));
            value >>= 7;
        }

        stream.WriteByte((byte)value);
    }

    private static int ReadPrefixedInteger(ReadOnlySpan<byte> buffer, ref int index, int prefixBits)
    {
        int limit = (1 << prefixBits) - 1;
        int value = buffer[index++] & limit;

        if (value < limit)
        {
            return value;
        }

        int shift = 0;
        byte octet;

        do
        {
            octet = buffer[index++];
            value += (octet & 0x7F) << shift;
            shift += 7;
        }
        while ((octet & 0x80) != 0);

        return value;
    }

    // RFC 9204 §4.1.2: the Huffman flag sits just above the length's prefix.
    private static string ReadString(ReadOnlySpan<byte> buffer, ref int index, int prefixBits)
    {
        bool huffman = (buffer[index] & (1 << prefixBits)) != 0;
        int length = ReadPrefixedInteger(buffer, ref index, prefixBits);
        ReadOnlySpan<byte> octets = buffer.Slice(index, length);
        index += length;

        return huffman ? DecodeHuffman(octets) : Encoding.ASCII.GetString(octets);
    }

    private static string DecodeHuffman(ReadOnlySpan<byte> octets)
    {
        StringBuilder decoded = new();
        int totalBits = octets.Length * 8;
        int position = 0;

        while (totalBits - position >= 5)
        {
            bool matched = false;

            for (int length = 5; length <= 15 && length <= totalBits - position; length++)
            {
                if (_huffmanCodes.TryGetValue((length, ReadBits(octets, position, length)), out char symbol))
                {
                    decoded.Append(symbol);
                    position += length;
                    matched = true;
                    break;
                }
            }

            if (!matched)
            {
                // RFC 7541 §5.2: what remains must be the EOS prefix, fewer than 8 one-bits.
                if (totalBits - position < 8 && ReadBits(octets, position, totalBits - position) == (1 << (totalBits - position)) - 1)
                {
                    break;
                }

                throw new InvalidDataException("The Huffman string holds a symbol the test decoder does not support.");
            }
        }

        return decoded.ToString();
    }

    private static int ReadBits(ReadOnlySpan<byte> octets, int position, int count)
    {
        int value = 0;

        for (int bit = position; bit < position + count; bit++)
        {
            value = (value << 1) | ((octets[bit / 8] >> (7 - (bit % 8))) & 1);
        }

        return value;
    }

    private static Dictionary<(int Length, int Code), char> BuildHuffmanCodes()
    {
        Dictionary<(int Length, int Code), char> codes = new();
        int code = 0;
        int previousLength = _huffmanLengths[0].Length;

        foreach ((int length, string symbols) in _huffmanLengths)
        {
            code <<= length - previousLength;
            previousLength = length;

            foreach (char symbol in symbols)
            {
                codes[(length, code)] = symbol;
                code++;
            }
        }

        return codes;
    }
}

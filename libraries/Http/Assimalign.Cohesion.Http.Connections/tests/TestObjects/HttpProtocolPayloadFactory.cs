using System;
using System.Collections.Generic;
using System.IO;
using System.Text;

using Assimalign.Cohesion.Http.Connections.Internal.Http3.QPack;

namespace Assimalign.Cohesion.Http.Connections.Tests.TestObjects;

internal static class HttpProtocolPayloadFactory
{
    public static byte[] CreateHttp1Request(string requestText)
    {
        return Encoding.ASCII.GetBytes(requestText);
    }

    public static byte[] CreateHttp2Request(
        int streamId,
        string method,
        string path,
        string scheme,
        string authority,
        IDictionary<string, string>? headers = null,
        byte[]? body = null)
    {
        using MemoryStream buffer = new();
        byte[] preface = Encoding.ASCII.GetBytes("PRI * HTTP/2.0\r\n\r\nSM\r\n\r\n");
        buffer.Write(preface, 0, preface.Length);
        WriteHttp2Frame(buffer, 0, 0x4, 0, Array.Empty<byte>());

        byte[] headerBlock = EncodeHttp2Headers(method, path, scheme, authority, headers);
        byte headerFlags = 0x4;

        if (body is null || body.Length == 0)
        {
            headerFlags |= 0x1;
        }

        WriteHttp2Frame(buffer, streamId, 0x1, headerFlags, headerBlock);

        if (body is { Length: > 0 })
        {
            WriteHttp2Frame(buffer, streamId, 0x0, 0x1, body);
        }

        return buffer.ToArray();
    }

    public static IReadOnlyList<(long FrameType, byte[] Payload)> ParseHttp2Frames(byte[] payload)
    {
        List<(long FrameType, byte[] Payload)> frames = new();
        int index = 0;

        while (index < payload.Length)
        {
            int length = (payload[index] << 16) | (payload[index + 1] << 8) | payload[index + 2];
            byte type = payload[index + 3];
            index += 9;
            byte[] framePayload = payload.AsSpan(index, length).ToArray();
            index += length;
            frames.Add((type, framePayload));
        }

        return frames;
    }

    public static byte[] CreateHttp3Request(
        string method,
        string path,
        string scheme,
        string authority,
        IDictionary<string, string>? headers = null,
        byte[]? body = null)
    {
        using MemoryStream buffer = new();
        byte[] headerBlock = EncodeHttp3Headers(method, path, scheme, authority, headers);
        WriteHttp3Frame(buffer, 0x1, headerBlock);

        if (body is { Length: > 0 })
        {
            WriteHttp3Frame(buffer, 0x0, body);
        }

        return buffer.ToArray();
    }

    /// <summary>
    /// Builds the bytes a peer would send on its HTTP/3 control stream: the
    /// control stream-type prefix (0x00) followed by a SETTINGS frame carrying
    /// the supplied identifier/value pairs.
    /// </summary>
    public static byte[] CreateHttp3ControlStream(params (long Id, long Value)[] settings)
    {
        using MemoryStream settingsPayload = new();
        foreach ((long id, long value) in settings)
        {
            WriteQuicInteger(settingsPayload, id);
            WriteQuicInteger(settingsPayload, value);
        }

        using MemoryStream buffer = new();
        WriteQuicInteger(buffer, 0x00); // control stream type
        WriteHttp3Frame(buffer, 0x4 /* SETTINGS */, settingsPayload.ToArray());
        return buffer.ToArray();
    }

    /// <summary>
    /// Builds the bytes a peer would send on its HTTP/3 control stream: the
    /// control stream-type prefix (0x00), a SETTINGS frame carrying the supplied
    /// identifier/value pairs, then the supplied trailing control frames
    /// (e.g. GOAWAY, MAX_PUSH_ID) appended after SETTINGS. Used to exercise the
    /// post-SETTINGS drain path.
    /// </summary>
    public static byte[] CreateHttp3ControlStreamWithControlFrames(
        (long Id, long Value)[] settings,
        params (long FrameType, byte[] Payload)[] trailingFrames)
    {
        using MemoryStream settingsPayload = new();
        foreach ((long id, long value) in settings)
        {
            WriteQuicInteger(settingsPayload, id);
            WriteQuicInteger(settingsPayload, value);
        }

        using MemoryStream buffer = new();
        WriteQuicInteger(buffer, 0x00); // control stream type
        WriteHttp3Frame(buffer, 0x4 /* SETTINGS */, settingsPayload.ToArray());

        foreach ((long frameType, byte[] payload) in trailingFrames)
        {
            WriteHttp3Frame(buffer, frameType, payload);
        }

        return buffer.ToArray();
    }

    /// <summary>
    /// Encodes a single value as a QUIC variable-length integer payload — the
    /// body of a GOAWAY (a stream/push ID) or MAX_PUSH_ID (a push ID) frame.
    /// </summary>
    public static byte[] CreateHttp3VarintPayload(long value)
    {
        using MemoryStream buffer = new();
        WriteQuicInteger(buffer, value);
        return buffer.ToArray();
    }

    /// <summary>
    /// Builds the bytes for a generic HTTP/3 unidirectional stream: the
    /// stream-type prefix followed by an optional raw payload. Used to drive
    /// QPACK / push / unknown stream-type handling in tests.
    /// </summary>
    public static byte[] CreateHttp3UnidirectionalStream(long streamType, byte[]? payload = null)
    {
        using MemoryStream buffer = new();
        WriteQuicInteger(buffer, streamType);
        if (payload is { Length: > 0 })
        {
            buffer.Write(payload, 0, payload.Length);
        }

        return buffer.ToArray();
    }

    /// <summary>
    /// Builds an HTTP/3 request HEADERS frame whose QPACK field section is
    /// the supplied field lines, encoded verbatim as Literal Field Line With
    /// Literal Name (RFC 9204 §4.5.6) — preserving the exact order and casing
    /// passed in, with no validation. Used to drive malformed field-section
    /// tests (uppercase names, mis-ordered or duplicate pseudo-headers).
    /// </summary>
    public static byte[] CreateHttp3RequestRaw(params (string Name, string Value)[] fields)
        => CreateHttp3RequestRaw(huffman: false, fields);

    /// <summary>
    /// As <see cref="CreateHttp3RequestRaw(ValueTuple{string, string}[])"/>,
    /// optionally Huffman-coding the literal name and value octets so the
    /// decoder's Huffman path is exercised end to end.
    /// </summary>
    public static byte[] CreateHttp3RequestRaw(bool huffman, params (string Name, string Value)[] fields)
    {
        using MemoryStream section = new();
        section.WriteByte(0x00); // Required Insert Count = 0
        section.WriteByte(0x00); // S = 0, Delta Base = 0

        foreach ((string name, string value) in fields)
        {
            WriteHttp3LiteralFieldLine(section, name, value, huffman);
        }

        using MemoryStream buffer = new();
        WriteHttp3Frame(buffer, 0x1 /* HEADERS */, section.ToArray());
        return buffer.ToArray();
    }

    /// <summary>
    /// Builds the bytes a peer would send on its QPACK encoder stream (RFC 9204
    /// §4.2): the stream-type prefix (0x02) followed by the supplied encoder
    /// instructions concatenated in order.
    /// </summary>
    public static byte[] CreateHttp3QPackEncoderStream(params byte[][] instructions)
    {
        using MemoryStream body = new();
        foreach (byte[] instruction in instructions)
        {
            body.Write(instruction, 0, instruction.Length);
        }

        return CreateHttp3UnidirectionalStream(0x02 /* QPACK encoder */, body.ToArray());
    }

    /// <summary>Set Dynamic Table Capacity encoder instruction (RFC 9204 §4.3.1): <c>001</c> + 5-bit prefix.</summary>
    public static byte[] QPackSetCapacity(long capacity)
    {
        using MemoryStream buffer = new();
        WritePrefixedInteger(buffer, checked((int)capacity), 5, 0b0010_0000);
        return buffer.ToArray();
    }

    /// <summary>Insert with Literal Name encoder instruction (RFC 9204 §4.3.3): <c>01</c> + H + 5-bit name, then value.</summary>
    public static byte[] QPackInsertWithLiteralName(string name, string value)
    {
        using MemoryStream buffer = new();
        WritePrefixedString(buffer, name, 5, 0b0100_0000); // 01 H(=0) name-len(5).
        WritePrefixedString(buffer, value, 7, 0x00);
        return buffer.ToArray();
    }

    /// <summary>Insert with (static) Name Reference encoder instruction (RFC 9204 §4.3.2): <c>1</c> T(=1) + 6-bit index, then value.</summary>
    public static byte[] QPackInsertWithStaticName(int staticIndex, string value)
    {
        using MemoryStream buffer = new();
        WritePrefixedInteger(buffer, staticIndex, 6, 0b1100_0000); // 1 T(=1) index(6).
        WritePrefixedString(buffer, value, 7, 0x00);
        return buffer.ToArray();
    }

    /// <summary>Duplicate encoder instruction (RFC 9204 §4.3.4): <c>000</c> + 5-bit relative index.</summary>
    public static byte[] QPackDuplicate(int relativeIndex)
    {
        using MemoryStream buffer = new();
        WritePrefixedInteger(buffer, relativeIndex, 5, 0x00);
        return buffer.ToArray();
    }

    /// <summary>
    /// Builds a QPACK field section (no HEADERS frame wrapper) with a zero Field
    /// Section Prefix followed by the supplied literal field lines. Used to drive
    /// the decoder-state path directly.
    /// </summary>
    public static byte[] CreateHttp3FieldSection(params (string Name, string Value)[] fields)
    {
        using MemoryStream section = new();
        section.WriteByte(0x00); // Required Insert Count = 0.
        section.WriteByte(0x00); // S = 0, Delta Base = 0.

        foreach ((string name, string value) in fields)
        {
            WriteHttp3LiteralFieldLine(section, name, value, huffman: false);
        }

        return section.ToArray();
    }

    /// <summary>
    /// Builds an HTTP/3 request HEADERS frame whose QPACK field section carries a
    /// custom Field Section Prefix, literal field lines, then dynamic indexed
    /// field lines (RFC 9204 §4.5.2, T = 0) referencing the supplied relative
    /// indices. Used to drive the dynamic-table decode path.
    /// </summary>
    public static byte[] CreateHttp3DynamicRequest(
        long encodedRequiredInsertCount,
        byte deltaBaseByte,
        (string Name, string Value)[] literalFields,
        params int[] dynamicRelativeIndices)
    {
        using MemoryStream section = new();
        WritePrefixedInteger(section, checked((int)encodedRequiredInsertCount), 8, 0x00); // Encoded Required Insert Count.
        section.WriteByte(deltaBaseByte);                                                  // Delta Base (S + 7-bit).

        foreach ((string name, string value) in literalFields)
        {
            WriteHttp3LiteralFieldLine(section, name, value, huffman: false);
        }

        foreach (int relativeIndex in dynamicRelativeIndices)
        {
            // §4.5.2 Indexed Field Line, dynamic (T = 0): 1 0 index(6).
            WritePrefixedInteger(section, relativeIndex, 6, 0b1000_0000);
        }

        using MemoryStream buffer = new();
        WriteHttp3Frame(buffer, 0x1 /* HEADERS */, section.ToArray());
        return buffer.ToArray();
    }

    /// <summary>
    /// Huffman-codes an ASCII string using the canonical HPACK/QPACK code
    /// (RFC 7541 Appendix B), MSB-first with all-ones EOS padding.
    /// </summary>
    public static byte[] HuffmanEncode(string value)
    {
        using MemoryStream output = new();
        ulong bits = 0;
        int count = 0;

        foreach (char c in value)
        {
            (uint code, byte length) = Internal.Http2.HPack.HPackHuffmanCodes.Table[(byte)c];
            bits = (bits << length) | code;
            count += length;

            while (count >= 8)
            {
                count -= 8;
                output.WriteByte((byte)(bits >> count));
            }
        }

        if (count > 0)
        {
            int pad = 8 - count;
            output.WriteByte((byte)((bits << pad) | ((1UL << pad) - 1)));
        }

        return output.ToArray();
    }

    private static void WriteHttp3LiteralFieldLine(Stream stream, string name, string value, bool huffman)
    {
        // §4.5.6 Literal Field Line With Literal Name: 0 0 1 N H len(3).
        WriteHttp3StringLiteral(stream, name, prefixBits: 3, basePattern: 0b0010_0000, huffman);
        WriteHttp3StringLiteral(stream, value, prefixBits: 7, basePattern: 0x00, huffman);
    }

    private static void WriteHttp3StringLiteral(Stream stream, string value, int prefixBits, byte basePattern, bool huffman)
    {
        byte huffmanFlag = huffman ? (byte)(1 << prefixBits) : (byte)0;
        byte[] octets = huffman ? HuffmanEncode(value) : Encoding.ASCII.GetBytes(value);
        QPackPrefixedInteger.Encode(stream, octets.Length, prefixBits, (byte)(basePattern | huffmanFlag));
        stream.Write(octets, 0, octets.Length);
    }

    public static IReadOnlyList<(long FrameType, byte[] Payload)> ParseHttp3Frames(byte[] payload)
    {
        List<(long FrameType, byte[] Payload)> frames = new();
        int index = 0;

        while (index < payload.Length)
        {
            long type = DecodeQuicInteger(payload, ref index);
            long length = DecodeQuicInteger(payload, ref index);
            byte[] framePayload = payload.AsSpan(index, checked((int)length)).ToArray();
            index += (int)length;
            frames.Add((type, framePayload));
        }

        return frames;
    }

    /// <summary>
    /// Parses the bytes written to an HTTP/3 unidirectional stream: the leading
    /// stream-type varint followed by zero or more length-delimited frames.
    /// Used to decode the server's outbound control stream.
    /// </summary>
    public static (long StreamType, IReadOnlyList<(long FrameType, byte[] Payload)> Frames) ParseHttp3UnidirectionalStream(byte[] payload)
    {
        int index = 0;
        long streamType = DecodeQuicInteger(payload, ref index);

        List<(long FrameType, byte[] Payload)> frames = new();
        while (index < payload.Length)
        {
            long type = DecodeQuicInteger(payload, ref index);
            long length = DecodeQuicInteger(payload, ref index);
            byte[] framePayload = payload.AsSpan(index, checked((int)length)).ToArray();
            index += (int)length;
            frames.Add((type, framePayload));
        }

        return (streamType, frames);
    }

    /// <summary>
    /// Decodes an HTTP/3 SETTINGS frame payload into its identifier→value pairs
    /// (RFC 9114 §7.2.4).
    /// </summary>
    public static IReadOnlyDictionary<long, long> DecodeHttp3Settings(byte[] settingsPayload)
    {
        Dictionary<long, long> settings = new();
        int index = 0;

        while (index < settingsPayload.Length)
        {
            long id = DecodeQuicInteger(settingsPayload, ref index);
            long value = DecodeQuicInteger(settingsPayload, ref index);
            settings[id] = value;
        }

        return settings;
    }

    public static Dictionary<string, string> DecodeLiteralHttp2Headers(byte[] headerBlock)
    {
        Dictionary<string, string> headers = new(StringComparer.OrdinalIgnoreCase);
        int index = 0;

        while (index < headerBlock.Length)
        {
            byte current = headerBlock[index];

            if ((current & 0x80) != 0)
            {
                int staticIndex = DecodePrefixedInteger(headerBlock, ref index, 7);
                (string staticName, string staticValue) = GetHttp2StaticHeader(staticIndex);
                headers[staticName] = staticValue;
                continue;
            }

            if ((current & 0x20) != 0)
            {
                DecodePrefixedInteger(headerBlock, ref index, 5);
                continue;
            }

            bool literalWithIndexing = (current & 0x40) != 0;
            int prefixLength = literalWithIndexing ? 6 : 4;
            int nameIndex = DecodePrefixedInteger(headerBlock, ref index, prefixLength);
            string name = nameIndex == 0 ? DecodePrefixedString(headerBlock, ref index, 7) : GetHttp2StaticHeaderName(nameIndex);
            string value = DecodePrefixedString(headerBlock, ref index, 7);
            headers[name] = value;
        }

        return headers;
    }

    public static Dictionary<string, string> DecodeLiteralHttp3Headers(byte[] headerBlock)
    {
        // Delegate to the production QPACK decoder so this helper validates
        // whatever representation the encoder actually emits (static indexed
        // field lines, name references, or literals).
        Dictionary<string, string> headers = new(StringComparer.OrdinalIgnoreCase);

        foreach ((string name, string value) in QPackFieldSectionDecoder.Decode(headerBlock))
        {
            headers[name] = value;
        }

        return headers;
    }

    private static byte[] EncodeHttp2Headers(string method, string path, string scheme, string authority, IDictionary<string, string>? headers)
    {
        using MemoryStream buffer = new();
        WriteHttp2MethodHeader(buffer, method);
        WriteHttp2PathHeader(buffer, path);
        WriteHttp2SchemeHeader(buffer, scheme);
        WriteHttp2IndexedNameHeader(buffer, 1, authority);

        if (headers is not null)
        {
            foreach (KeyValuePair<string, string> header in headers)
            {
                WriteHttp2LiteralHeader(buffer, header.Key.ToLowerInvariant(), header.Value);
            }
        }

        return buffer.ToArray();
    }

    private static (string Name, string Value) GetHttp2StaticHeader(int index) => index switch
    {
        1 => (":authority", string.Empty),
        2 => (":method", "GET"),
        3 => (":method", "POST"),
        4 => (":path", "/"),
        5 => (":path", "/index.html"),
        6 => (":scheme", "http"),
        7 => (":scheme", "https"),
        8 => (":status", "200"),
        9 => (":status", "204"),
        10 => (":status", "206"),
        11 => (":status", "304"),
        12 => (":status", "400"),
        13 => (":status", "404"),
        14 => (":status", "500"),
        28 => ("content-length", string.Empty),
        31 => ("content-type", string.Empty),
        32 => ("cookie", string.Empty),
        38 => ("host", string.Empty),
        46 => ("location", string.Empty),
        54 => ("server", string.Empty),
        55 => ("set-cookie", string.Empty),
        _ => throw new InvalidOperationException($"The test helper does not support the HPACK static index '{index}'.")
    };

    private static string GetHttp2StaticHeaderName(int index) => GetHttp2StaticHeader(index).Name;

    private static byte[] EncodeHttp3Headers(string method, string path, string scheme, string authority, IDictionary<string, string>? headers)
    {
        using MemoryStream buffer = new();
        WriteQuicInteger(buffer, 0);
        WriteQuicInteger(buffer, 0);
        WriteHttp3LiteralHeader(buffer, ":method", method);
        WriteHttp3LiteralHeader(buffer, ":path", path);
        WriteHttp3LiteralHeader(buffer, ":scheme", scheme);
        WriteHttp3LiteralHeader(buffer, ":authority", authority);

        if (headers is not null)
        {
            foreach (KeyValuePair<string, string> header in headers)
            {
                WriteHttp3LiteralHeader(buffer, header.Key.ToLowerInvariant(), header.Value);
            }
        }

        return buffer.ToArray();
    }

    /// <summary>
    /// Builds a HEADERS frame for <paramref name="streamId"/> containing the
    /// supplied <paramref name="fields"/> encoded as HPACK literal-with-no-
    /// indexing entries (RFC 7541 §6.2.2). The caller controls every byte —
    /// including field order, name casing, and pseudo-header presence —
    /// which is what field-section validation tests need.
    /// </summary>
    public static byte[] CreateHttp2HeadersFrame(int streamId, byte flags, params (string Name, string Value)[] fields)
    {
        using MemoryStream payload = new();
        foreach ((string name, string value) in fields)
        {
            WriteHttp2PassthroughLiteralHeader(payload, name, value);
        }

        using MemoryStream output = new();
        WriteHttp2Frame(output, streamId, 0x1 /* HEADERS */, flags, payload.ToArray());
        return output.ToArray();
    }

    private static void WriteHttp2PassthroughLiteralHeader(Stream stream, string name, string value)
    {
        // HPACK §6.2.2 literal header field without indexing: 4-bit prefix,
        // top nibble 0000, then length-prefixed name and value as 7-bit
        // prefixed strings with no Huffman encoding (H=0).
        WritePrefixedInteger(stream, 0, 4, 0x00);
        WritePrefixedString(stream, name, 7, 0x00);
        WritePrefixedString(stream, value, 7, 0x00);
    }

    private static void WriteHttp2Frame(Stream stream, int streamId, byte type, byte flags, byte[] payload)
    {
        byte[] header =
        {
            (byte)((payload.Length >> 16) & 0xFF),
            (byte)((payload.Length >> 8) & 0xFF),
            (byte)(payload.Length & 0xFF),
            type,
            flags,
            (byte)((streamId >> 24) & 0x7F),
            (byte)((streamId >> 16) & 0xFF),
            (byte)((streamId >> 8) & 0xFF),
            (byte)(streamId & 0xFF)
        };

        stream.Write(header, 0, header.Length);

        if (payload.Length > 0)
        {
            stream.Write(payload, 0, payload.Length);
        }
    }

    private static void WriteHttp2LiteralHeader(Stream stream, string name, string value)
    {
        WritePrefixedInteger(stream, 0, 4, 0x00);
        WritePrefixedString(stream, name, 7, 0x00);
        WritePrefixedString(stream, value, 7, 0x00);
    }

    private static void WriteHttp2IndexedHeader(Stream stream, int index)
    {
        WritePrefixedInteger(stream, index, 7, 0x80);
    }

    private static void WriteHttp2IndexedNameHeader(Stream stream, int index, string value)
    {
        WritePrefixedInteger(stream, index, 4, 0x00);
        WritePrefixedString(stream, value, 7, 0x00);
    }

    private static void WriteHttp2MethodHeader(Stream stream, string method)
    {
        if (string.Equals(method, "GET", StringComparison.OrdinalIgnoreCase))
        {
            WriteHttp2IndexedHeader(stream, 2);
            return;
        }

        if (string.Equals(method, "POST", StringComparison.OrdinalIgnoreCase))
        {
            WriteHttp2IndexedHeader(stream, 3);
            return;
        }

        WriteHttp2IndexedNameHeader(stream, 2, method);
    }

    private static void WriteHttp2PathHeader(Stream stream, string path)
    {
        if (string.Equals(path, "/", StringComparison.Ordinal))
        {
            WriteHttp2IndexedHeader(stream, 4);
            return;
        }

        WriteHttp2IndexedNameHeader(stream, 4, path);
    }

    private static void WriteHttp2SchemeHeader(Stream stream, string scheme)
    {
        if (string.Equals(scheme, "http", StringComparison.OrdinalIgnoreCase))
        {
            WriteHttp2IndexedHeader(stream, 6);
            return;
        }

        if (string.Equals(scheme, "https", StringComparison.OrdinalIgnoreCase))
        {
            WriteHttp2IndexedHeader(stream, 7);
            return;
        }

        WriteHttp2IndexedNameHeader(stream, 6, scheme);
    }

    private static void WriteHttp3Frame(Stream stream, long type, byte[] payload)
    {
        WriteQuicInteger(stream, type);
        WriteQuicInteger(stream, payload.Length);
        stream.Write(payload, 0, payload.Length);
    }

    private static void WriteHttp3LiteralHeader(Stream stream, string name, string value)
    {
        WritePrefixedString(stream, name, 3, 0x30);
        WritePrefixedString(stream, value, 7, 0x00);
    }

    private static void WritePrefixedString(Stream stream, string value, int prefixLength, byte prefixMask)
    {
        byte[] bytes = Encoding.ASCII.GetBytes(value);
        WritePrefixedInteger(stream, bytes.Length, prefixLength, prefixMask);
        stream.Write(bytes, 0, bytes.Length);
    }

    private static string DecodePrefixedString(byte[] buffer, ref int index, int prefixLength)
    {
        // RFC 7541 §5.2 — the bit immediately above the length prefix is the
        // Huffman flag. Capture it before the length prefix advances the index.
        bool huffman = (buffer[index] & (1 << prefixLength)) != 0;
        int length = DecodePrefixedInteger(buffer, ref index, prefixLength);
        ReadOnlySpan<byte> octets = buffer.AsSpan(index, length);
        index += length;

        return huffman
            ? Encoding.Latin1.GetString(Internal.Http2.HPack.HPackHuffmanDecoder.Decode(octets))
            : Encoding.ASCII.GetString(octets);
    }

    private static int DecodePrefixedInteger(byte[] buffer, ref int index, int prefixLength)
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

        throw new InvalidOperationException("The prefixed integer was incomplete.");
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

    private static long DecodeQuicInteger(byte[] buffer, ref int index)
    {
        byte first = buffer[index++];
        int length = (first >> 6) switch
        {
            0 => 1,
            1 => 2,
            2 => 4,
            _ => 8
        };
        ulong value = (ulong)(first & 0x3F);

        for (int offset = 1; offset < length; offset++)
        {
            value = (value << 8) | buffer[index++];
        }

        return (long)value;
    }

    private static void WriteQuicInteger(Stream stream, long value)
    {
        if (value < 64)
        {
            stream.WriteByte((byte)value);
            return;
        }

        if (value < 16384)
        {
            ushort encoded = (ushort)(value | 0x4000);
            stream.WriteByte((byte)(encoded >> 8));
            stream.WriteByte((byte)encoded);
            return;
        }

        // 4-byte form (RFC 9000 §16) — needed for the RFC 9218 PRIORITY_UPDATE
        // frame types (0xF0700 / 0xF0701), which exceed the 2-byte range.
        if (value < 1_073_741_824)
        {
            uint encoded = (uint)(value | 0x80000000);
            stream.WriteByte((byte)(encoded >> 24));
            stream.WriteByte((byte)(encoded >> 16));
            stream.WriteByte((byte)(encoded >> 8));
            stream.WriteByte((byte)encoded);
            return;
        }

        throw new InvalidOperationException("The test payload factory only supports QUIC integers below 2^30.");
    }

    /// <summary>
    /// Builds an RFC 9218 §7.2 HTTP/3 PRIORITY_UPDATE frame payload: the
    /// Prioritized Element ID as a QUIC variable-length integer followed by the
    /// ASCII Priority Field Value.
    /// </summary>
    public static byte[] CreateHttp3PriorityUpdatePayload(long prioritizedElementId, string priorityFieldValue)
    {
        using MemoryStream buffer = new();
        WriteQuicInteger(buffer, prioritizedElementId);
        byte[] fieldValue = Encoding.ASCII.GetBytes(priorityFieldValue);
        buffer.Write(fieldValue, 0, fieldValue.Length);
        return buffer.ToArray();
    }
}

using System;
using System.Collections.Generic;
using System.IO;
using System.Text;

namespace Assimalign.Cohesion.Http.Transports.Tests.TestObjects;

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
        Dictionary<string, string> headers = new(StringComparer.OrdinalIgnoreCase);
        int index = 0;

        DecodeQuicInteger(headerBlock, ref index);
        DecodeQuicInteger(headerBlock, ref index);

        while (index < headerBlock.Length)
        {
            string name = DecodePrefixedString(headerBlock, ref index, 3);
            string value = DecodePrefixedString(headerBlock, ref index, 7);
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
        int length = DecodePrefixedInteger(buffer, ref index, prefixLength);
        string value = Encoding.ASCII.GetString(buffer, index, length);
        index += length;
        return value;
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
        int length = first >> 6 switch
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

        throw new InvalidOperationException("The test payload factory only supports small QUIC integers.");
    }
}

using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.Text;

namespace Assimalign.Cohesion.Database.Protocol;

/// <summary>
/// Shared payload primitives: big-endian integers and length-prefixed UTF-8
/// strings, with strict bounds checks so malformed payloads fail loudly.
/// </summary>
internal static class ProtocolPayload
{
    internal static void WriteString(List<byte> buffer, string value)
    {
        byte[] bytes = Encoding.UTF8.GetBytes(value);
        WriteInt32(buffer, bytes.Length);
        buffer.AddRange(bytes);
    }

    internal static void WriteInt32(List<byte> buffer, int value)
    {
        Span<byte> scratch = stackalloc byte[sizeof(int)];
        BinaryPrimitives.WriteInt32BigEndian(scratch, value);
        buffer.AddRange(scratch.ToArray());
    }

    internal static void WriteInt64(List<byte> buffer, long value)
    {
        Span<byte> scratch = stackalloc byte[sizeof(long)];
        BinaryPrimitives.WriteInt64BigEndian(scratch, value);
        buffer.AddRange(scratch.ToArray());
    }

    internal static string ReadString(ReadOnlySpan<byte> payload, ref int position)
    {
        int length = ReadInt32(payload, ref position);

        if (length < 0 || position + length > payload.Length)
        {
            throw new ProtocolException("Malformed payload: invalid string length.");
        }

        string value = Encoding.UTF8.GetString(payload.Slice(position, length));
        position += length;
        return value;
    }

    internal static int ReadInt32(ReadOnlySpan<byte> payload, ref int position)
    {
        if (position + sizeof(int) > payload.Length)
        {
            throw new ProtocolException("Malformed payload: truncated integer.");
        }

        int value = BinaryPrimitives.ReadInt32BigEndian(payload[position..]);
        position += sizeof(int);
        return value;
    }

    internal static long ReadInt64(ReadOnlySpan<byte> payload, ref int position)
    {
        if (position + sizeof(long) > payload.Length)
        {
            throw new ProtocolException("Malformed payload: truncated integer.");
        }

        long value = BinaryPrimitives.ReadInt64BigEndian(payload[position..]);
        position += sizeof(long);
        return value;
    }

    internal static ushort ReadUInt16(ReadOnlySpan<byte> payload, ref int position)
    {
        if (position + sizeof(ushort) > payload.Length)
        {
            throw new ProtocolException("Malformed payload: truncated integer.");
        }

        ushort value = BinaryPrimitives.ReadUInt16BigEndian(payload[position..]);
        position += sizeof(ushort);
        return value;
    }

    internal static void WriteUInt16(List<byte> buffer, ushort value)
    {
        Span<byte> scratch = stackalloc byte[sizeof(ushort)];
        BinaryPrimitives.WriteUInt16BigEndian(scratch, value);
        buffer.AddRange(scratch.ToArray());
    }
}

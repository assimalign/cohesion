using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.Text;

namespace Assimalign.Cohesion.Database.Protocol;

/// <summary>
/// Shared payload primitives: big-endian integers and length-prefixed UTF-8
/// strings, with strict bounds checks so malformed payloads fail loudly.
/// </summary>
public static class ProtocolPayload
{
    /// <summary>Appends a big-endian length-prefixed UTF-8 string.</summary>
    /// <param name="buffer">The destination payload.</param>
    /// <param name="value">The value to encode.</param>
    public static void WriteString(List<byte> buffer, string value)
    {
        byte[] bytes = Encoding.UTF8.GetBytes(value);
        WriteInt32(buffer, bytes.Length);
        buffer.AddRange(bytes);
    }

    /// <summary>Appends a big-endian Int32 value.</summary>
    /// <param name="buffer">The destination payload.</param>
    /// <param name="value">The value to encode.</param>
    public static void WriteInt32(List<byte> buffer, int value)
    {
        Span<byte> scratch = stackalloc byte[sizeof(int)];
        BinaryPrimitives.WriteInt32BigEndian(scratch, value);
        buffer.AddRange(scratch.ToArray());
    }

    /// <summary>Appends a big-endian Int64 value.</summary>
    /// <param name="buffer">The destination payload.</param>
    /// <param name="value">The value to encode.</param>
    public static void WriteInt64(List<byte> buffer, long value)
    {
        Span<byte> scratch = stackalloc byte[sizeof(long)];
        BinaryPrimitives.WriteInt64BigEndian(scratch, value);
        buffer.AddRange(scratch.ToArray());
    }

    /// <summary>Reads a length-prefixed UTF-8 string.</summary>
    /// <param name="payload">The encoded payload.</param>
    /// <param name="position">The current offset, advanced on success.</param>
    /// <returns>The decoded value.</returns>
    /// <exception cref="ProtocolException">The payload is truncated or malformed.</exception>
    public static string ReadString(ReadOnlySpan<byte> payload, ref int position)
    {
        int length = ReadInt32(payload, ref position);

        if (length < 0 || (uint)position > (uint)payload.Length || length > payload.Length - position)
        {
            throw new ProtocolException("Malformed payload: invalid string length.");
        }

        string value = Encoding.UTF8.GetString(payload.Slice(position, length));
        position += length;
        return value;
    }

    /// <summary>Reads a big-endian Int32 value.</summary>
    /// <param name="payload">The encoded payload.</param>
    /// <param name="position">The current offset, advanced on success.</param>
    /// <returns>The decoded value.</returns>
    /// <exception cref="ProtocolException">The payload is truncated or malformed.</exception>
    public static int ReadInt32(ReadOnlySpan<byte> payload, ref int position)
    {
        if ((uint)position > (uint)payload.Length || sizeof(int) > payload.Length - position)
        {
            throw new ProtocolException("Malformed payload: truncated integer.");
        }

        int value = BinaryPrimitives.ReadInt32BigEndian(payload[position..]);
        position += sizeof(int);
        return value;
    }

    /// <summary>Reads a big-endian Int64 value.</summary>
    /// <param name="payload">The encoded payload.</param>
    /// <param name="position">The current offset, advanced on success.</param>
    /// <returns>The decoded value.</returns>
    /// <exception cref="ProtocolException">The payload is truncated or malformed.</exception>
    public static long ReadInt64(ReadOnlySpan<byte> payload, ref int position)
    {
        if ((uint)position > (uint)payload.Length || sizeof(long) > payload.Length - position)
        {
            throw new ProtocolException("Malformed payload: truncated integer.");
        }

        long value = BinaryPrimitives.ReadInt64BigEndian(payload[position..]);
        position += sizeof(long);
        return value;
    }

    /// <summary>Reads a big-endian UInt16 value.</summary>
    /// <param name="payload">The encoded payload.</param>
    /// <param name="position">The current offset, advanced on success.</param>
    /// <returns>The decoded value.</returns>
    /// <exception cref="ProtocolException">The payload is truncated or malformed.</exception>
    public static ushort ReadUInt16(ReadOnlySpan<byte> payload, ref int position)
    {
        if ((uint)position > (uint)payload.Length || sizeof(ushort) > payload.Length - position)
        {
            throw new ProtocolException("Malformed payload: truncated integer.");
        }

        ushort value = BinaryPrimitives.ReadUInt16BigEndian(payload[position..]);
        position += sizeof(ushort);
        return value;
    }

    /// <summary>Appends a big-endian UInt16 value.</summary>
    /// <param name="buffer">The destination payload.</param>
    /// <param name="value">The value to encode.</param>
    public static void WriteUInt16(List<byte> buffer, ushort value)
    {
        Span<byte> scratch = stackalloc byte[sizeof(ushort)];
        BinaryPrimitives.WriteUInt16BigEndian(scratch, value);
        buffer.AddRange(scratch.ToArray());
    }
}

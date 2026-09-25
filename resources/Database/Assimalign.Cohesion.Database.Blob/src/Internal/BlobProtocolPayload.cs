using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.Text;
using Assimalign.Cohesion.Database.Protocol;

namespace Assimalign.Cohesion.Database.Blob.Internal;

internal static class BlobProtocolPayload
{
    private const int MaxStringByteLength = 65_535;
    private static readonly UTF8Encoding _utf8 = new(false, true);

    internal static void WriteString(List<byte> buffer, string value, bool allowEmpty)
    {
        if (value is null || (!allowEmpty && value.Length == 0))
        {
            throw new ProtocolException("A Blob metadata string is missing.");
        }
        try
        {
            int length = _utf8.GetByteCount(value);
            if (length > MaxStringByteLength)
            {
                throw new ProtocolException("A Blob metadata string exceeds 65,535 UTF-8 bytes.");
            }
            Span<byte> prefix = stackalloc byte[sizeof(int)];
            BinaryPrimitives.WriteInt32BigEndian(prefix, length);
            buffer.AddRange(prefix.ToArray());
            buffer.AddRange(_utf8.GetBytes(value));
        }
        catch (EncoderFallbackException exception)
        {
            throw new ProtocolException("A Blob metadata string contains invalid Unicode.", exception);
        }
    }

    internal static string ReadString(ReadOnlySpan<byte> payload, ref int position, bool allowEmpty)
    {
        if (payload.Length - position < sizeof(int))
        {
            throw new ProtocolException("Truncated Blob metadata string length.");
        }
        int length = BinaryPrimitives.ReadInt32BigEndian(payload[position..]);
        position += sizeof(int);
        if (length < 0 || length > MaxStringByteLength || length > payload.Length - position || (!allowEmpty && length == 0))
        {
            throw new ProtocolException("Invalid Blob metadata string length.");
        }
        try
        {
            string value = _utf8.GetString(payload.Slice(position, length));
            position += length;
            return value;
        }
        catch (DecoderFallbackException exception)
        {
            throw new ProtocolException("A Blob metadata string contains invalid UTF-8.", exception);
        }
    }

    internal static void WriteInt64(List<byte> buffer, long value)
    {
        Span<byte> bytes = stackalloc byte[sizeof(long)];
        BinaryPrimitives.WriteInt64BigEndian(bytes, value);
        buffer.AddRange(bytes.ToArray());
    }

    internal static long ReadInt64(ReadOnlySpan<byte> payload, ref int position)
    {
        if (payload.Length - position < sizeof(long))
        {
            throw new ProtocolException("Truncated Blob transfer length.");
        }
        long value = BinaryPrimitives.ReadInt64BigEndian(payload[position..]);
        position += sizeof(long);
        return value;
    }

    internal static void RequireEnd(ReadOnlySpan<byte> payload, int position)
    {
        if (position != payload.Length)
        {
            throw new ProtocolException("Unexpected trailing bytes in a Blob payload.");
        }
    }
}

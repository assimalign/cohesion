using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.Text;
using Assimalign.Cohesion.Database.Protocol;

namespace Assimalign.Cohesion.Database.Blob.Protocol.Internal;

internal static class BlobProtocolPayload
{
    private const int MaxStringByteLength = 65_535;
    private static readonly UTF8Encoding _utf8 = new(false, true);

    internal static void WriteString(List<byte> buffer, string value, bool allowEmpty)
    {
        if (value is null || (!allowEmpty && value.Length == 0))
        {
            throw new ProtocolException(ErrorMessages.ProtocolPayloadWriteStringProtocol);
        }
        try
        {
            int length = _utf8.GetByteCount(value);
            if (length > MaxStringByteLength)
            {
                throw new ProtocolException(ErrorMessages.ProtocolPayloadWriteStringProtocol2);
            }
            Span<byte> prefix = stackalloc byte[sizeof(int)];
            BinaryPrimitives.WriteInt32BigEndian(prefix, length);
            buffer.AddRange(prefix.ToArray());
            buffer.AddRange(_utf8.GetBytes(value));
        }
        catch (EncoderFallbackException exception)
        {
            throw new ProtocolException(ErrorMessages.ProtocolPayloadWriteStringProtocol3, exception);
        }
    }

    internal static string ReadString(ReadOnlySpan<byte> payload, ref int position, bool allowEmpty)
    {
        if (payload.Length - position < sizeof(int))
        {
            throw new ProtocolException(ErrorMessages.BlobProtocolPayloadReadStringProtocol);
        }
        int length = BinaryPrimitives.ReadInt32BigEndian(payload[position..]);
        position += sizeof(int);
        if (length < 0 || length > MaxStringByteLength || length > payload.Length - position || (!allowEmpty && length == 0))
        {
            throw new ProtocolException(ErrorMessages.BlobProtocolPayloadReadStringProtocol2);
        }
        try
        {
            string value = _utf8.GetString(payload.Slice(position, length));
            position += length;
            return value;
        }
        catch (DecoderFallbackException exception)
        {
            throw new ProtocolException(ErrorMessages.BlobProtocolPayloadReadStringProtocol3, exception);
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
            throw new ProtocolException(ErrorMessages.BlobProtocolPayloadReadInt64Protocol);
        }
        long value = BinaryPrimitives.ReadInt64BigEndian(payload[position..]);
        position += sizeof(long);
        return value;
    }

    internal static void RequireEnd(ReadOnlySpan<byte> payload, int position)
    {
        if (position != payload.Length)
        {
            throw new ProtocolException(ErrorMessages.BlobProtocolPayloadRequireEndProtocol);
        }
    }
}

using System;
using System.Buffers;
using System.Buffers.Binary;

namespace Assimalign.Cohesion.Content.Bmff.Internal;

// Field I/O shares Content.Binary's exact-read implementation through BmffBinary.
internal static class BmffFields
{
    internal static byte U8(BmffStream s) { Span<byte> b = stackalloc byte[1]; return BmffBinary.ReadField(s, b).ReadByte(); }
    internal static ushort U16(BmffStream s) { Span<byte> b = stackalloc byte[2]; return BmffBinary.ReadField(s, b).ReadUInt16BigEndian(); }
    internal static uint U32(BmffStream s) { Span<byte> b = stackalloc byte[4]; return BmffBinary.ReadField(s, b).ReadUInt32BigEndian(); }
    internal static ulong U64(BmffStream s) { Span<byte> b = stackalloc byte[8]; return BmffBinary.ReadField(s, b).ReadUInt64BigEndian(); }
    internal static short I16(BmffStream s) => unchecked((short)U16(s));
    internal static int I32(BmffStream s) => unchecked((int)U32(s));
    internal static long I64(BmffStream s) => unchecked((long)U64(s));
    internal static void U8(BmffStream s, byte v) { Span<byte> b = stackalloc byte[1]; b[0] = v; s.Write(b); }
    internal static void U16(BmffStream s, ushort v) { Span<byte> b = stackalloc byte[2]; BinaryPrimitives.WriteUInt16BigEndian(b, v); s.Write(b); }
    internal static void U32(BmffStream s, uint v) { Span<byte> b = stackalloc byte[4]; BinaryPrimitives.WriteUInt32BigEndian(b, v); s.Write(b); }
    internal static void U64(BmffStream s, ulong v) { Span<byte> b = stackalloc byte[8]; BinaryPrimitives.WriteUInt64BigEndian(b, v); s.Write(b); }
    internal static void I16(BmffStream s, short v) => U16(s, unchecked((ushort)v));
    internal static void I32(BmffStream s, int v) => U32(s, unchecked((uint)v));
    internal static void I64(BmffStream s, long v) => U64(s, unchecked((ulong)v));

    internal static void Version(byte version, uint flags, byte maximum, uint allowedFlags, long position)
    {
        if (version > maximum || (flags & ~allowedFlags) != 0)
        {
            throw new ContentFormatException(ErrorMessages.UnsupportedVersion, ContentFormatErrorCode.Unsupported, position);
        }
    }
    internal static int Count(BmffStream s, ulong count, int wireSize, int memorySize)
    {
        if (count > (ulong)s.Options.MaxTableEntryCount || count > int.MaxValue)
        {
            throw new ContentFormatException(ErrorMessages.EntryLimit, ContentFormatErrorCode.LimitExceeded, s.Offset + s.Position);
        }

        if (wireSize > 0 && count > (ulong)(s.Remaining / wireSize))
        {
            throw new ContentFormatException(ErrorMessages.EntryBounds, ContentFormatErrorCode.InvalidStructure, s.Offset + s.Position);
        }

        Reserve(s, (long)count * memorySize);
        return (int)count;
    }
    private static void Reserve(BmffStream s, long length)
    {
        if (length < 0 || length > s.Options.MaxBufferedPayloadSize - s.AllocatedFieldBytes)
        {
            throw new ContentFormatException(ErrorMessages.BufferLimit, ContentFormatErrorCode.LimitExceeded, s.Offset + s.Position);
        }

        s.AllocatedFieldBytes += length;
    }
    internal static byte[] Bytes(BmffStream s, long length)
    {
        if (length < 0 || length > s.Remaining)
        {
            throw new ContentFormatException(ErrorMessages.TruncatedHeader, ContentFormatErrorCode.Truncated, s.Offset + s.Length);
        }

        Reserve(s, length);
        var result = new byte[(int)length];
        BmffBinary.ReadField(s, result);
        return result;
    }
    internal static byte[] Text(BmffStream s)
    {
        if (s.Remaining > s.Options.MaxBufferedPayloadSize)
        {
            throw new ContentFormatException(ErrorMessages.BufferLimit, ContentFormatErrorCode.LimitExceeded, s.Offset + s.Position);
        }

        var buffer = ArrayPool<byte>.Shared.Rent(Math.Max(1, (int)s.Remaining));
        try
        {
            var length = 0;
            while (true)
            {
                var value = U8(s);
                if (value == 0)
                {
                    break;
                }

                buffer[length++] = value;
            }
            Reserve(s, length);
            return buffer.AsSpan(0, length).ToArray();
        }
        finally { ArrayPool<byte>.Shared.Return(buffer); }
    }
    internal static void Text(BmffStream s, byte[] value) { s.Write(value); U8(s, 0); }
    internal static ulong Sized(BmffStream s, int size)
    {
        ulong result = 0;
        for (var i = 0; i < size; i++)
        {
            result = (result << 8) | U8(s);
        }

        return result;
    }
    internal static void Sized(BmffStream s, ulong value, int size)
    {
        Width(value, size, s.Offset + s.Position);
        for (var i = size - 1; i >= 0; i--)
        {
            U8(s, (byte)(value >> (i * 8)));
        }
    }
    internal static void Width(ulong value, int size, long position)
    {
        if (size < 0 || size > 8 || (size < 8 && value >= (1UL << (size * 8))))
        {
            throw new ContentFormatException(ErrorMessages.FieldRange, ContentFormatErrorCode.InvalidValue, position);
        }
    }
    internal static void ArrayLength(Array? value, int length, long position)
    {
        if (value is null || value.Length != length)
        {
            throw new ContentFormatException(ErrorMessages.FieldRange, ContentFormatErrorCode.InvalidValue, position);
        }
    }
    internal static long TextSize(byte[] bytes, long position)
    {
        if (bytes.AsSpan().IndexOf((byte)0) >= 0)
        {
            throw new ContentFormatException(ErrorMessages.FieldRange, ContentFormatErrorCode.InvalidValue, position);
        }

        return bytes.LongLength + 1;
    }
    internal static string Language(ushort value) => new string([(char)(((value >> 10) & 31) + 0x60), (char)(((value >> 5) & 31) + 0x60), (char)((value & 31) + 0x60)]);
}

using System;
using System.Buffers.Binary;
using System.Text;

using Assimalign.Cohesion.Content.Binary;

namespace Assimalign.Cohesion.Content.Ebml.Internal;

internal static class EbmlValueCodec
{
    internal static readonly UTF8Encoding Utf8 = new(false, true);

    internal static bool Matches(EbmlElementType type, object? value) => type switch
    {
        EbmlElementType.UnsignedInteger => value is ulong,
        EbmlElementType.SignedInteger or EbmlElementType.Date => value is long,
        EbmlElementType.Float => value is double,
        EbmlElementType.AsciiString or EbmlElementType.Utf8String => value is string,
        EbmlElementType.Binary => value is BinarySegment,
        EbmlElementType.MasterElement => value is null,
        _ => false
    };

    internal static bool ValidLength(EbmlElementType type, long size) => type switch
    {
        EbmlElementType.SignedInteger or EbmlElementType.UnsignedInteger => size is >= 0 and <= 8,
        EbmlElementType.Float => size is 0 or 4 or 8,
        EbmlElementType.Date => size is 0 or 8,
        _ => size >= 0
    };

    internal static object Decode(EbmlElementType type, ReadOnlySpan<byte> bytes, long position)
    {
        if (!ValidLength(type, bytes.Length))
        {
            throw new EbmlDataFormatException(ErrorMessages.InvalidSize, ContentFormatErrorCode.InvalidValue, position);
        }

        var reader = new BinaryContentReader(bytes, position);
        switch (type)
        {
            case EbmlElementType.UnsignedInteger:
                return bytes.IsEmpty ? 0UL : reader.ReadUIntBigEndian(bytes.Length);
            case EbmlElementType.SignedInteger:
            case EbmlElementType.Date:
                if (bytes.IsEmpty)
                {
                    return 0L;
                }

                var value = reader.ReadUIntBigEndian(bytes.Length);
                if (bytes.Length < 8 && (bytes[0] & 0x80) != 0)
                {
                    value |= ulong.MaxValue << (bytes.Length * 8);
                }

                return unchecked((long)value);
            case EbmlElementType.Float:
                return bytes.Length == 0 ? 0d : bytes.Length == 4 ? (double)reader.ReadSingleBigEndian() : reader.ReadDoubleBigEndian();
            case EbmlElementType.AsciiString:
            case EbmlElementType.Utf8String:
                var terminator = bytes.IndexOf((byte)0);
                if (terminator >= 0)
                {
                    bytes = bytes[..terminator]; // RFC 8794 section 13: ignore everything after the first NUL.
                }

                if (type == EbmlElementType.AsciiString)
                {
                    for (var i = 0; i < bytes.Length; i++)
                    {
                        if (bytes[i] < 0x20 || bytes[i] > 0x7e)
                        {
                            throw new EbmlDataFormatException(ErrorMessages.InvalidValue, ContentFormatErrorCode.InvalidValue, position + i);
                        }
                    }
                }

                try { return Utf8.GetString(bytes); }
                catch (DecoderFallbackException error)
                {
                    throw new EbmlDataFormatException(ErrorMessages.InvalidValue, ContentFormatErrorCode.InvalidValue, position, error);
                }
            default:
                throw new ArgumentException(ErrorMessages.InvalidType, nameof(type));
        }
    }

    internal static int GetLength(EbmlElementType type, object value, int requested = -1)
    {
        if (!Matches(type, value))
        {
            throw new ArgumentException(ErrorMessages.InvalidType, nameof(value));
        }

        ArgumentOutOfRangeException.ThrowIfLessThan(requested, -1);
        var minimal = 0;
        switch (type)
        {
            case EbmlElementType.UnsignedInteger:
                for (var remaining = (ulong)value; remaining != 0; remaining >>= 8)
                {
                    minimal++;
                }

                break;
            case EbmlElementType.SignedInteger:
                var number = (long)value;
                if (number != 0)
                {
                    minimal = 1;
                    while (minimal < 8 && (number < -(1L << (minimal * 8 - 1)) || number >= (1L << (minimal * 8 - 1))))
                    {
                        minimal++;
                    }
                }
                break;
            case EbmlElementType.Date:
                minimal = (long)value == 0 ? 0 : 8;
                break;
            case EbmlElementType.Float:
                var floating = (double)value;
                minimal = BitConverter.DoubleToInt64Bits(floating) == 0 ? 0 : (double)(float)floating == floating ? 4 : 8;
                break;
            case EbmlElementType.AsciiString:
            case EbmlElementType.Utf8String:
                var text = (string)value;
                foreach (var character in text)
                {
                    if (character == 0 || (type == EbmlElementType.AsciiString && (character < 0x20 || character > 0x7e)))
                    {
                        throw new ArgumentException(ErrorMessages.InvalidValue, nameof(value));
                    }
                }

                try { minimal = Utf8.GetByteCount(text); }
                catch (EncoderFallbackException error) { throw new ArgumentException(ErrorMessages.InvalidValue, nameof(value), error); }
                break;
            default: throw new ArgumentException(ErrorMessages.InvalidType, nameof(type));
        }
        var length = requested == -1 ? minimal : requested;
        if (!ValidLength(type, length) || length < minimal)
        {
            throw new ArgumentException(ErrorMessages.InvalidSize, nameof(requested));
        }

        return length;
    }

    internal static int GetWireLength(EbmlElementDefinition? definition, EbmlElementType type, object value, int requested = -1)
    {
        var fallback = definition?.DefaultValue;
        if (fallback is not null && Equals(fallback, value) && requested is -1 or 0 && definition!.MinimumLength == 0)
        {
            return 0;
        }

        var length = GetLength(type, value, requested);
        if (fallback is not null && length == 0 && !Equals(fallback, value))
        {
            if (requested == 0)
            {
                throw new ArgumentException(ErrorMessages.InvalidValue, nameof(requested));
            }

            length = type == EbmlElementType.Date ? 8 : type == EbmlElementType.Float ? 4 : 1;
        }
        if (requested == -1 && definition is not null && definition.MinimumLength > length)
        {
            if (definition.MinimumLength > Array.MaxLength)
            {
                throw new ArgumentException(ErrorMessages.InvalidSize);
            }

            length = (int)definition.MinimumLength;
        }
        if (!ValidLength(type, length))
        {
            throw new ArgumentException(ErrorMessages.InvalidSize);
        }

        return length;
    }

    internal static void Encode(EbmlElementType type, object value, Span<byte> bytes)
    {
        bytes.Clear();
        if (bytes.IsEmpty)
        {
            return;
        }

        if (type is EbmlElementType.AsciiString or EbmlElementType.Utf8String)
        {
            Utf8.GetBytes((string)value, bytes);
            return;
        }
        if (type == EbmlElementType.Float)
        {
            if (bytes.Length == 4)
            {
                BinaryPrimitives.WriteSingleBigEndian(bytes, (float)(double)value);
            }
            else
            {
                BinaryPrimitives.WriteDoubleBigEndian(bytes, (double)value);
            }

            return;
        }
        var bits = type == EbmlElementType.UnsignedInteger ? (ulong)value : unchecked((ulong)(long)value);
        for (var index = bytes.Length - 1; index >= 0; index--, bits >>= 8)
        {
            bytes[index] = (byte)bits;
        }
    }
}

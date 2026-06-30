using System;
using System.Buffers;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.Text;

namespace Assimalign.Cohesion.Amqp.Connections.Internal;

internal static partial class AmqpEncoding
{
    private static readonly Encoding Utf8 = Encoding.UTF8;

    private static void WriteValue(ArrayBufferWriter<byte> writer, object? value)
    {
        switch (value)
        {
            case null:
                WriteByte(writer, 0x40);
                return;
            case bool boolean:
                WriteByte(writer, boolean ? (byte) 0x41 : (byte) 0x42);
                return;
            case byte ubyte:
                WriteByte(writer, 0x50);
                WriteByte(writer, ubyte);
                return;
            case sbyte @byte:
                WriteByte(writer, 0x51);
                WriteByte(writer, unchecked((byte) @byte));
                return;
            case ushort ushortValue:
                WriteByte(writer, 0x60);
                WriteUInt16(writer, ushortValue);
                return;
            case short shortValue:
                WriteByte(writer, 0x61);
                WriteInt16(writer, shortValue);
                return;
            case uint uintValue:
                WriteUInt(writer, uintValue);
                return;
            case int intValue:
                WriteInt(writer, intValue);
                return;
            case ulong ulongValue:
                WriteULong(writer, ulongValue);
                return;
            case long longValue:
                WriteLong(writer, longValue);
                return;
            case float singleValue:
                WriteByte(writer, 0x72);
                WriteInt32(writer, BitConverter.SingleToInt32Bits(singleValue));
                return;
            case double doubleValue:
                WriteByte(writer, 0x82);
                WriteInt64(writer, BitConverter.DoubleToInt64Bits(doubleValue));
                return;
            case Guid guidValue:
                WriteByte(writer, 0x98);
                WriteBytes(writer, guidValue.ToByteArray());
                return;
            case DateTimeOffset timestampValue:
                WriteByte(writer, 0x83);
                WriteInt64(writer, timestampValue.ToUnixTimeMilliseconds());
                return;
            case string stringValue:
                WriteString(writer, stringValue, isSymbol: false);
                return;
            case AmqpSymbol symbolValue:
                WriteString(writer, symbolValue.Value, isSymbol: true);
                return;
            case byte[] byteArray:
                WriteBinary(writer, byteArray);
                return;
            case ReadOnlyMemory<byte> memory:
                WriteBinary(writer, memory.Span);
                return;
            case AmqpError error:
                WriteDescribedList(writer, ErrorDescriptor, new object?[] { error.Condition, error.Description, error.Info });
                return;
            case AmqpSource source:
                WriteDescribedList(writer, SourceDescriptor, new object?[]
                {
                    source.Address,
                    source.Durable,
                    source.ExpiryPolicy,
                    source.Timeout,
                    source.Dynamic,
                    source.DynamicNodeProperties,
                    source.DistributionMode,
                    source.Filter,
                    source.DefaultOutcome,
                    source.Outcomes,
                    source.Capabilities
                });
                return;
            case AmqpTarget target:
                WriteDescribedList(writer, TargetDescriptor, new object?[]
                {
                    target.Address,
                    target.Durable,
                    target.ExpiryPolicy,
                    target.Timeout,
                    target.Dynamic,
                    target.DynamicNodeProperties,
                    target.Capabilities
                });
                return;
            case AmqpDescribedValue describedValue:
                WriteDescribedValue(writer, describedValue.Descriptor, describedValue.Value);
                return;
            case IReadOnlyDictionary<AmqpSymbol, object?> symbolMap:
                WriteMap(writer, symbolMap);
                return;
            case IReadOnlyDictionary<string, object?> stringMap:
                WriteMap(writer, stringMap);
                return;
            case IReadOnlyDictionary<object, object?> objectMap:
                WriteMap(writer, objectMap);
                return;
            case IReadOnlyList<AmqpSymbol> symbolArray:
                WriteSymbolArray(writer, symbolArray);
                return;
            case object?[] objectArray:
                WriteList(writer, objectArray);
                return;
            case IReadOnlyList<object?> objectList:
                WriteList(writer, objectList);
                return;
            default:
                throw new AmqpProtocolException($"The AMQP value type '{value.GetType().FullName}' is not supported.");
        }
    }

    private static object? ReadValue(ref AmqpBufferReader reader)
    {
        byte formatCode = reader.ReadByte();
        return ReadValueWithFormatCode(ref reader, formatCode);
    }

    private static object? ReadValueWithFormatCode(ref AmqpBufferReader reader, byte formatCode)
    {
        return formatCode switch
        {
            0x00 => ReadDescribedValue(ref reader),
            0x40 => null,
            0x41 => true,
            0x42 => false,
            0x50 => reader.ReadByte(),
            0x51 => unchecked((sbyte) reader.ReadByte()),
            0x52 => (uint) reader.ReadByte(),
            0x53 => (ulong) reader.ReadByte(),
            0x54 => unchecked((sbyte) reader.ReadByte()),
            0x55 => unchecked((sbyte) reader.ReadByte()),
            0x60 => reader.ReadUInt16(),
            0x61 => reader.ReadInt16(),
            0x70 => reader.ReadUInt32(),
            0x71 => reader.ReadInt32(),
            0x72 => reader.ReadSingle(),
            0x73 => char.ConvertFromUtf32(reader.ReadInt32()),
            0x80 => reader.ReadUInt64(),
            0x81 => reader.ReadInt64(),
            0x82 => reader.ReadDouble(),
            0x83 => DateTimeOffset.FromUnixTimeMilliseconds(reader.ReadInt64()),
            0x98 => new Guid(reader.ReadBytes(16).ToArray()),
            0x43 => (uint) 0,
            0x44 => (ulong) 0,
            0xa0 => reader.ReadBytes(reader.ReadByte()).ToArray(),
            0xb0 => reader.ReadBytes(reader.ReadInt32()).ToArray(),
            0xa1 => Utf8.GetString(reader.ReadBytes(reader.ReadByte())),
            0xb1 => Utf8.GetString(reader.ReadBytes(reader.ReadInt32())),
            0xa3 => new AmqpSymbol(Utf8.GetString(reader.ReadBytes(reader.ReadByte()))),
            0xb3 => new AmqpSymbol(Utf8.GetString(reader.ReadBytes(reader.ReadInt32()))),
            0x45 => Array.Empty<object?>(),
            0xc0 => ReadList(ref reader, reader.ReadByte(), false),
            0xd0 => ReadList(ref reader, reader.ReadInt32(), true),
            0xc1 => ReadMap(ref reader, reader.ReadByte(), false),
            0xd1 => ReadMap(ref reader, reader.ReadInt32(), true),
            0xe0 => ReadArray(ref reader, reader.ReadByte(), false),
            0xf0 => ReadArray(ref reader, reader.ReadInt32(), true),
            _ => throw new AmqpProtocolException($"The AMQP format code '0x{formatCode:x2}' is not supported.")
        };
    }

    private static (object Descriptor, object? Value) ReadDescribedRaw(ref AmqpBufferReader reader)
    {
        byte descriptorFormatCode = reader.ReadByte();
        object? descriptor = ReadValueWithFormatCode(ref reader, descriptorFormatCode);

        if (descriptor is null)
        {
            throw new AmqpProtocolException("AMQP described values require a non-null descriptor.");
        }

        object? value = ReadValue(ref reader);

        return (descriptor, value);
    }

    private static object ReadDescribedValue(ref AmqpBufferReader reader)
    {
        (object descriptor, object? value) = ReadDescribedRaw(ref reader);

        if (descriptor is ulong numericDescriptor)
        {
            return numericDescriptor switch
            {
                ErrorDescriptor => ReadError(value),
                SourceDescriptor => ReadSource(value),
                TargetDescriptor => ReadTarget(value),
                _ => new AmqpDescribedValue(numericDescriptor, value)
            };
        }

        return new AmqpDescribedValue(descriptor, value);
    }

    private static void WriteDescribedValue(ArrayBufferWriter<byte> writer, object descriptor, object? value)
    {
        WriteByte(writer, 0x00);
        WriteValue(writer, descriptor is string descriptorString ? new AmqpSymbol(descriptorString) : descriptor);
        WriteValue(writer, value);
    }

    private static void WriteDescribedList(ArrayBufferWriter<byte> writer, ulong descriptor, IReadOnlyList<object?> fields)
    {
        WriteByte(writer, 0x00);
        WriteULong(writer, descriptor);
        WriteList(writer, fields, trimTrailingNulls: true);
    }

    private static object?[] ReadList(ref AmqpBufferReader reader, int size, bool wideCount)
    {
        if (size == 0)
        {
            return Array.Empty<object?>();
        }

        ReadOnlySpan<byte> bytes = reader.ReadBytes(size);
        AmqpBufferReader payloadReader = new(bytes);
        int count = wideCount ? payloadReader.ReadInt32() : payloadReader.ReadByte();
        object?[] values = new object?[count];

        for (int i = 0; i < count; i++)
        {
            values[i] = ReadValue(ref payloadReader);
        }

        return values;
    }

    private static Dictionary<object, object?> ReadMap(ref AmqpBufferReader reader, int size, bool wideCount)
    {
        ReadOnlySpan<byte> bytes = reader.ReadBytes(size);
        AmqpBufferReader payloadReader = new(bytes);
        int count = wideCount ? payloadReader.ReadInt32() : payloadReader.ReadByte();

        if ((count & 1) != 0)
        {
            throw new AmqpProtocolException("AMQP maps require an even number of elements.");
        }

        Dictionary<object, object?> values = new(count / 2);

        for (int i = 0; i < count; i += 2)
        {
            object? key = ReadValue(ref payloadReader);
            object? mapValue = ReadValue(ref payloadReader);

            if (key is null)
            {
                throw new AmqpProtocolException("AMQP map keys cannot be null.");
            }

            values[key] = mapValue;
        }

        return values;
    }

    private static object ReadArray(ref AmqpBufferReader reader, int size, bool wideCount)
    {
        ReadOnlySpan<byte> bytes = reader.ReadBytes(size);
        AmqpBufferReader payloadReader = new(bytes);
        int count = wideCount ? payloadReader.ReadInt32() : payloadReader.ReadByte();
        byte elementFormatCode = payloadReader.ReadByte();
        object?[] values = new object?[count];

        for (int i = 0; i < count; i++)
        {
            values[i] = ReadValueWithFormatCode(ref payloadReader, elementFormatCode);
        }

        if (elementFormatCode is 0xa3 or 0xb3)
        {
            AmqpSymbol[] symbols = new AmqpSymbol[count];

            for (int i = 0; i < count; i++)
            {
                symbols[i] = (AmqpSymbol) values[i]!;
            }

            return symbols;
        }

        return values;
    }

    private static void WriteList(ArrayBufferWriter<byte> writer, IReadOnlyList<object?> values, bool trimTrailingNulls = false)
    {
        int count = values.Count;

        if (trimTrailingNulls)
        {
            while (count > 0 && values[count - 1] is null)
            {
                count--;
            }
        }

        if (count == 0)
        {
            WriteByte(writer, 0x45);
            return;
        }

        ArrayBufferWriter<byte> payloadWriter = new();

        for (int i = 0; i < count; i++)
        {
            WriteValue(payloadWriter, values[i]);
        }

        if (payloadWriter.WrittenCount + 1 <= byte.MaxValue && count <= byte.MaxValue)
        {
            WriteByte(writer, 0xc0);
            WriteByte(writer, unchecked((byte) (payloadWriter.WrittenCount + 1)));
            WriteByte(writer, unchecked((byte) count));
        }
        else
        {
            WriteByte(writer, 0xd0);
            WriteInt32(writer, payloadWriter.WrittenCount + 4);
            WriteInt32(writer, count);
        }

        WriteBytes(writer, payloadWriter.WrittenSpan);
    }

    private static void WriteMap<T>(ArrayBufferWriter<byte> writer, IEnumerable<KeyValuePair<T, object?>> values)
    {
        ArrayBufferWriter<byte> payloadWriter = new();
        int count = 0;

        foreach (KeyValuePair<T, object?> value in values)
        {
            WriteValue(payloadWriter, value.Key);
            WriteValue(payloadWriter, value.Value);
            count += 2;
        }

        if (payloadWriter.WrittenCount + 1 <= byte.MaxValue && count <= byte.MaxValue)
        {
            WriteByte(writer, 0xc1);
            WriteByte(writer, unchecked((byte) (payloadWriter.WrittenCount + 1)));
            WriteByte(writer, unchecked((byte) count));
        }
        else
        {
            WriteByte(writer, 0xd1);
            WriteInt32(writer, payloadWriter.WrittenCount + 4);
            WriteInt32(writer, count);
        }

        WriteBytes(writer, payloadWriter.WrittenSpan);
    }

    private static void WriteSymbolArray(ArrayBufferWriter<byte> writer, IReadOnlyList<AmqpSymbol> values)
    {
        ArrayBufferWriter<byte> payloadWriter = new();
        bool useEightBitLength = true;

        for (int i = 0; i < values.Count; i++)
        {
            int length = Utf8.GetByteCount(values[i].Value);
            useEightBitLength &= length <= byte.MaxValue;
        }

        byte elementFormatCode = useEightBitLength ? (byte) 0xa3 : (byte) 0xb3;

        for (int i = 0; i < values.Count; i++)
        {
            byte[] utf8Bytes = Utf8.GetBytes(values[i].Value);

            if (useEightBitLength)
            {
                WriteByte(payloadWriter, unchecked((byte) utf8Bytes.Length));
            }
            else
            {
                WriteInt32(payloadWriter, utf8Bytes.Length);
            }

            WriteBytes(payloadWriter, utf8Bytes);
        }

        if (payloadWriter.WrittenCount + 2 <= byte.MaxValue && values.Count <= byte.MaxValue)
        {
            WriteByte(writer, 0xe0);
            WriteByte(writer, unchecked((byte) (payloadWriter.WrittenCount + 2)));
            WriteByte(writer, unchecked((byte) values.Count));
        }
        else
        {
            WriteByte(writer, 0xf0);
            WriteInt32(writer, payloadWriter.WrittenCount + 5);
            WriteInt32(writer, values.Count);
        }

        WriteByte(writer, elementFormatCode);
        WriteBytes(writer, payloadWriter.WrittenSpan);
    }

    private static void WriteUInt(ArrayBufferWriter<byte> writer, uint value)
    {
        if (value == 0)
        {
            WriteByte(writer, 0x43);
            return;
        }

        if (value <= byte.MaxValue)
        {
            WriteByte(writer, 0x52);
            WriteByte(writer, unchecked((byte) value));
            return;
        }

        WriteByte(writer, 0x70);
        WriteUInt32(writer, value);
    }

    private static void WriteInt(ArrayBufferWriter<byte> writer, int value)
    {
        if (value >= sbyte.MinValue && value <= sbyte.MaxValue)
        {
            WriteByte(writer, 0x54);
            WriteByte(writer, unchecked((byte) value));
            return;
        }

        WriteByte(writer, 0x71);
        WriteInt32(writer, value);
    }

    private static void WriteULong(ArrayBufferWriter<byte> writer, ulong value)
    {
        if (value == 0)
        {
            WriteByte(writer, 0x44);
            return;
        }

        if (value <= byte.MaxValue)
        {
            WriteByte(writer, 0x53);
            WriteByte(writer, unchecked((byte) value));
            return;
        }

        WriteByte(writer, 0x80);
        WriteUInt64(writer, value);
    }

    private static void WriteLong(ArrayBufferWriter<byte> writer, long value)
    {
        if (value >= sbyte.MinValue && value <= sbyte.MaxValue)
        {
            WriteByte(writer, 0x55);
            WriteByte(writer, unchecked((byte) value));
            return;
        }

        WriteByte(writer, 0x81);
        WriteInt64(writer, value);
    }

    private static void WriteString(ArrayBufferWriter<byte> writer, string value, bool isSymbol)
    {
        byte[] utf8Bytes = Utf8.GetBytes(value);
        byte smallCode = isSymbol ? (byte) 0xa3 : (byte) 0xa1;
        byte largeCode = isSymbol ? (byte) 0xb3 : (byte) 0xb1;

        if (utf8Bytes.Length <= byte.MaxValue)
        {
            WriteByte(writer, smallCode);
            WriteByte(writer, unchecked((byte) utf8Bytes.Length));
        }
        else
        {
            WriteByte(writer, largeCode);
            WriteInt32(writer, utf8Bytes.Length);
        }

        WriteBytes(writer, utf8Bytes);
    }

    private static void WriteBinary(ArrayBufferWriter<byte> writer, ReadOnlySpan<byte> value)
    {
        if (value.Length <= byte.MaxValue)
        {
            WriteByte(writer, 0xa0);
            WriteByte(writer, unchecked((byte) value.Length));
        }
        else
        {
            WriteByte(writer, 0xb0);
            WriteInt32(writer, value.Length);
        }

        WriteBytes(writer, value);
    }

    private static void WriteByte(ArrayBufferWriter<byte> writer, byte value)
    {
        Span<byte> span = writer.GetSpan(1);
        span[0] = value;
        writer.Advance(1);
    }

    private static void WriteBytes(ArrayBufferWriter<byte> writer, ReadOnlySpan<byte> values)
    {
        values.CopyTo(writer.GetSpan(values.Length));
        writer.Advance(values.Length);
    }

    private static void WriteInt16(ArrayBufferWriter<byte> writer, short value)
    {
        Span<byte> span = writer.GetSpan(2);
        BinaryPrimitives.WriteInt16BigEndian(span, value);
        writer.Advance(2);
    }

    private static void WriteUInt16(ArrayBufferWriter<byte> writer, ushort value)
    {
        Span<byte> span = writer.GetSpan(2);
        BinaryPrimitives.WriteUInt16BigEndian(span, value);
        writer.Advance(2);
    }

    private static void WriteInt32(ArrayBufferWriter<byte> writer, int value)
    {
        Span<byte> span = writer.GetSpan(4);
        BinaryPrimitives.WriteInt32BigEndian(span, value);
        writer.Advance(4);
    }

    private static void WriteUInt32(ArrayBufferWriter<byte> writer, uint value)
    {
        Span<byte> span = writer.GetSpan(4);
        BinaryPrimitives.WriteUInt32BigEndian(span, value);
        writer.Advance(4);
    }

    private static void WriteInt64(ArrayBufferWriter<byte> writer, long value)
    {
        Span<byte> span = writer.GetSpan(8);
        BinaryPrimitives.WriteInt64BigEndian(span, value);
        writer.Advance(8);
    }

    private static void WriteUInt64(ArrayBufferWriter<byte> writer, ulong value)
    {
        Span<byte> span = writer.GetSpan(8);
        BinaryPrimitives.WriteUInt64BigEndian(span, value);
        writer.Advance(8);
    }
}

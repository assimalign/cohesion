using System;
using System.Buffers.Binary;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.Text;

namespace Assimalign.Cohesion.Amqp.Connections.Internal;

/// <summary>
/// Encodes values in the AMQP 1.0 type system (Part 1, section 1.6), choosing the most compact encoding
/// each type allows.
/// </summary>
internal static class AmqpEncoder
{
    /// <summary>
    /// The deepest nesting of compound and described values the encoder and the reader accept. It bounds
    /// recursion over self-referencing or hostile input.
    /// </summary>
    public const int MaxNestingDepth = 64;

    private const int compoundHeaderSize = 9;

    public static void WriteNull(AmqpBufferWriter writer)
    {
        writer.WriteByte(AmqpFormatCode.Null);
    }

    public static void WriteBoolean(AmqpBufferWriter writer, bool value)
    {
        writer.WriteByte(value ? AmqpFormatCode.BooleanTrue : AmqpFormatCode.BooleanFalse);
    }

    public static void WriteUByte(AmqpBufferWriter writer, byte value)
    {
        Span<byte> span = writer.Reserve(2);
        span[0] = AmqpFormatCode.UByte;
        span[1] = value;
    }

    public static void WriteUShort(AmqpBufferWriter writer, ushort value)
    {
        Span<byte> span = writer.Reserve(3);
        span[0] = AmqpFormatCode.UShort;
        BinaryPrimitives.WriteUInt16BigEndian(span.Slice(1), value);
    }

    public static void WriteUInt(AmqpBufferWriter writer, uint value)
    {
        if (value == 0)
        {
            writer.WriteByte(AmqpFormatCode.UInt0);
        }
        else if (value <= byte.MaxValue)
        {
            Span<byte> span = writer.Reserve(2);
            span[0] = AmqpFormatCode.SmallUInt;
            span[1] = (byte)value;
        }
        else
        {
            Span<byte> span = writer.Reserve(5);
            span[0] = AmqpFormatCode.UInt;
            BinaryPrimitives.WriteUInt32BigEndian(span.Slice(1), value);
        }
    }

    public static void WriteULong(AmqpBufferWriter writer, ulong value)
    {
        if (value == 0)
        {
            writer.WriteByte(AmqpFormatCode.ULong0);
        }
        else if (value <= byte.MaxValue)
        {
            Span<byte> span = writer.Reserve(2);
            span[0] = AmqpFormatCode.SmallULong;
            span[1] = (byte)value;
        }
        else
        {
            Span<byte> span = writer.Reserve(9);
            span[0] = AmqpFormatCode.ULong;
            BinaryPrimitives.WriteUInt64BigEndian(span.Slice(1), value);
        }
    }

    public static void WriteByte(AmqpBufferWriter writer, sbyte value)
    {
        Span<byte> span = writer.Reserve(2);
        span[0] = AmqpFormatCode.Byte;
        span[1] = (byte)value;
    }

    public static void WriteShort(AmqpBufferWriter writer, short value)
    {
        Span<byte> span = writer.Reserve(3);
        span[0] = AmqpFormatCode.Short;
        BinaryPrimitives.WriteInt16BigEndian(span.Slice(1), value);
    }

    public static void WriteInt(AmqpBufferWriter writer, int value)
    {
        if (value is >= sbyte.MinValue and <= sbyte.MaxValue)
        {
            Span<byte> span = writer.Reserve(2);
            span[0] = AmqpFormatCode.SmallInt;
            span[1] = (byte)(sbyte)value;
        }
        else
        {
            Span<byte> span = writer.Reserve(5);
            span[0] = AmqpFormatCode.Int;
            BinaryPrimitives.WriteInt32BigEndian(span.Slice(1), value);
        }
    }

    public static void WriteLong(AmqpBufferWriter writer, long value)
    {
        if (value is >= sbyte.MinValue and <= sbyte.MaxValue)
        {
            Span<byte> span = writer.Reserve(2);
            span[0] = AmqpFormatCode.SmallLong;
            span[1] = (byte)(sbyte)value;
        }
        else
        {
            Span<byte> span = writer.Reserve(9);
            span[0] = AmqpFormatCode.Long;
            BinaryPrimitives.WriteInt64BigEndian(span.Slice(1), value);
        }
    }

    public static void WriteFloat(AmqpBufferWriter writer, float value)
    {
        Span<byte> span = writer.Reserve(5);
        span[0] = AmqpFormatCode.Float;
        BinaryPrimitives.WriteSingleBigEndian(span.Slice(1), value);
    }

    public static void WriteDouble(AmqpBufferWriter writer, double value)
    {
        Span<byte> span = writer.Reserve(9);
        span[0] = AmqpFormatCode.Double;
        BinaryPrimitives.WriteDoubleBigEndian(span.Slice(1), value);
    }

    public static void WriteDecimal32(AmqpBufferWriter writer, AmqpDecimal32 value)
    {
        Span<byte> span = writer.Reserve(5);
        span[0] = AmqpFormatCode.Decimal32;
        BinaryPrimitives.WriteUInt32BigEndian(span.Slice(1), value.Bits);
    }

    public static void WriteDecimal64(AmqpBufferWriter writer, AmqpDecimal64 value)
    {
        Span<byte> span = writer.Reserve(9);
        span[0] = AmqpFormatCode.Decimal64;
        BinaryPrimitives.WriteUInt64BigEndian(span.Slice(1), value.Bits);
    }

    public static void WriteDecimal128(AmqpBufferWriter writer, AmqpDecimal128 value)
    {
        Span<byte> span = writer.Reserve(17);
        span[0] = AmqpFormatCode.Decimal128;
        BinaryPrimitives.WriteUInt128BigEndian(span.Slice(1), value.Bits);
    }

    public static void WriteChar(AmqpBufferWriter writer, Rune value)
    {
        Span<byte> span = writer.Reserve(5);
        span[0] = AmqpFormatCode.Char;
        BinaryPrimitives.WriteUInt32BigEndian(span.Slice(1), (uint)value.Value);
    }

    public static void WriteTimestamp(AmqpBufferWriter writer, DateTimeOffset value)
    {
        Span<byte> span = writer.Reserve(9);
        span[0] = AmqpFormatCode.Timestamp;
        BinaryPrimitives.WriteInt64BigEndian(span.Slice(1), value.ToUnixTimeMilliseconds());
    }

    public static void WriteTimestamp(AmqpBufferWriter writer, DateTime value)
    {
        // An unspecified kind is taken as UTC, the clock AMQP timestamps count in.
        DateTime utc = value.Kind switch
        {
            DateTimeKind.Local => value.ToUniversalTime(),
            DateTimeKind.Unspecified => DateTime.SpecifyKind(value, DateTimeKind.Utc),
            _ => value
        };

        WriteTimestamp(writer, new DateTimeOffset(utc));
    }

    public static void WriteUuid(AmqpBufferWriter writer, Guid value)
    {
        Span<byte> span = writer.Reserve(17);
        span[0] = AmqpFormatCode.Uuid;

        // AMQP orders a UUID's octets as RFC 4122 does: big-endian.
        value.TryWriteBytes(span.Slice(1), bigEndian: true, out _);
    }

    public static void WriteBinary(AmqpBufferWriter writer, ReadOnlySpan<byte> value)
    {
        WriteVariableHeader(writer, value.Length, AmqpFormatCode.Binary8, AmqpFormatCode.Binary32);
        writer.Write(value);
    }

    public static void WriteString(AmqpBufferWriter writer, string? value)
    {
        if (value is null)
        {
            WriteNull(writer);
            return;
        }

        int length = Encoding.UTF8.GetByteCount(value);

        WriteVariableHeader(writer, length, AmqpFormatCode.String8, AmqpFormatCode.String32);
        Encoding.UTF8.GetBytes(value, writer.Reserve(length));
    }

    /// <summary>
    /// Writes a symbol; a default symbol, which holds no value, is written as <c>null</c>.
    /// </summary>
    public static void WriteSymbol(AmqpBufferWriter writer, AmqpSymbol value)
    {
        string? text = value.Value;

        if (text is null)
        {
            WriteNull(writer);
            return;
        }

        // A symbol is seven-bit ASCII, so its length in octets is its length in characters.
        WriteVariableHeader(writer, text.Length, AmqpFormatCode.Symbol8, AmqpFormatCode.Symbol32);
        Encoding.ASCII.GetBytes(text, writer.Reserve(text.Length));
    }

    /// <summary>
    /// Writes an array of symbols, or <c>null</c>.
    /// </summary>
    public static void WriteSymbolArray(AmqpBufferWriter writer, IReadOnlyList<AmqpSymbol>? values)
    {
        if (values is null)
        {
            WriteNull(writer);
            return;
        }

        bool narrow = true;

        for (int i = 0; i < values.Count; i++)
        {
            if ((values[i].Value?.Length ?? 0) > byte.MaxValue)
            {
                narrow = false;
                break;
            }
        }

        int start = BeginCompound(writer, AmqpFormatCode.Array32);

        writer.WriteByte(narrow ? AmqpFormatCode.Symbol8 : AmqpFormatCode.Symbol32);

        for (int i = 0; i < values.Count; i++)
        {
            string text = values[i].Value ?? string.Empty;

            if (narrow)
            {
                writer.WriteByte((byte)text.Length);
            }
            else
            {
                writer.WriteUInt32((uint)text.Length);
            }

            Encoding.ASCII.GetBytes(text, writer.Reserve(text.Length));
        }

        EndCompound(writer, start, values.Count, AmqpFormatCode.Array8);
    }

    /// <summary>
    /// Writes a map keyed by symbols, such as <c>fields</c> or annotations, or <c>null</c>.
    /// </summary>
    public static void WriteSymbolMap(AmqpBufferWriter writer, IReadOnlyDictionary<AmqpSymbol, object?>? map)
    {
        WriteSymbolMap(writer, map, 0);
    }

    /// <summary>
    /// Writes a map keyed by strings, such as <c>application-properties</c>, or <c>null</c>.
    /// </summary>
    public static void WriteStringMap(AmqpBufferWriter writer, IReadOnlyDictionary<string, object?>? map)
    {
        if (map is null)
        {
            WriteNull(writer);
            return;
        }

        int start = BeginCompound(writer, AmqpFormatCode.Map32);

        foreach (KeyValuePair<string, object?> entry in map)
        {
            WriteString(writer, entry.Key);
            WriteValue(writer, entry.Value, 1);
        }

        EndCompound(writer, start, map.Count * 2, AmqpFormatCode.Map8);
    }

    /// <summary>
    /// Writes a list of values, or <c>null</c>.
    /// </summary>
    public static void WriteList(AmqpBufferWriter writer, IReadOnlyList<object?>? list)
    {
        WriteList(writer, list, 0);
    }

    /// <summary>
    /// Writes the descriptor of a described type.
    /// </summary>
    public static void WriteDescriptor(AmqpBufferWriter writer, ulong code)
    {
        writer.WriteByte(AmqpFormatCode.Described);
        WriteULong(writer, code);
    }

    /// <summary>
    /// Writes any value the type system can represent.
    /// </summary>
    /// <exception cref="ArgumentException">Thrown when the value, or a value it contains, has no AMQP representation, or nests too deeply.</exception>
    public static void WriteValue(AmqpBufferWriter writer, object? value)
    {
        WriteValue(writer, value, 0);
    }

    internal static void WriteValue(AmqpBufferWriter writer, object? value, int depth)
    {
        switch (value)
        {
            case null:
                WriteNull(writer);
                return;
            case bool boolean:
                WriteBoolean(writer, boolean);
                return;
            case byte unsignedByte:
                WriteUByte(writer, unsignedByte);
                return;
            case sbyte signedByte:
                WriteByte(writer, signedByte);
                return;
            case ushort unsignedShort:
                WriteUShort(writer, unsignedShort);
                return;
            case short signedShort:
                WriteShort(writer, signedShort);
                return;
            case uint unsignedInt:
                WriteUInt(writer, unsignedInt);
                return;
            case int signedInt:
                WriteInt(writer, signedInt);
                return;
            case ulong unsignedLong:
                WriteULong(writer, unsignedLong);
                return;
            case long signedLong:
                WriteLong(writer, signedLong);
                return;
            case float single:
                WriteFloat(writer, single);
                return;
            case double number:
                WriteDouble(writer, number);
                return;
            case AmqpDecimal32 decimal32:
                WriteDecimal32(writer, decimal32);
                return;
            case AmqpDecimal64 decimal64:
                WriteDecimal64(writer, decimal64);
                return;
            case AmqpDecimal128 decimal128:
                WriteDecimal128(writer, decimal128);
                return;
            case Rune rune:
                WriteChar(writer, rune);
                return;
            case char character:
                WriteChar(writer, new Rune(character));
                return;
            case DateTimeOffset timestamp:
                WriteTimestamp(writer, timestamp);
                return;
            case DateTime dateTime:
                WriteTimestamp(writer, dateTime);
                return;
            case Guid uuid:
                WriteUuid(writer, uuid);
                return;
            case string text:
                WriteString(writer, text);
                return;
            case AmqpSymbol symbol:
                WriteSymbol(writer, symbol);
                return;
            case byte[] bytes:
                WriteBinary(writer, bytes);
                return;
            case ReadOnlyMemory<byte> readOnlyMemory:
                WriteBinary(writer, readOnlyMemory.Span);
                return;
            case Memory<byte> memory:
                WriteBinary(writer, memory.Span);
                return;
            case ArraySegment<byte> segment:
                WriteBinary(writer, segment);
                return;
            case AmqpDeliveryTag deliveryTag:
                WriteBinary(writer, deliveryTag.Value.Span);
                return;
        }

        ThrowIfTooDeep(depth);

        switch (value)
        {
            case AmqpDescribedValue described:
                WriteDescribed(writer, described, depth + 1);
                return;
            case AmqpError error:
                AmqpTypeCodec.WriteError(writer, error, depth + 1);
                return;
            case AmqpDeliveryState state:
                AmqpTypeCodec.WriteDeliveryState(writer, state, depth + 1);
                return;
            case AmqpSource source:
                AmqpTypeCodec.WriteSource(writer, source, depth + 1);
                return;
            case AmqpTarget target:
                AmqpTypeCodec.WriteTarget(writer, target, depth + 1);
                return;
            case AmqpCoordinator coordinator:
                AmqpTypeCodec.WriteCoordinator(writer, coordinator);
                return;
            case AmqpDeclare declare:
                AmqpTypeCodec.WriteDeclare(writer, declare, depth + 1);
                return;
            case AmqpDischarge discharge:
                AmqpTypeCodec.WriteDischarge(writer, discharge);
                return;
            case AmqpSymbol[] symbols:
                WriteSymbolArray(writer, symbols);
                return;
            case string[] strings:
                WriteStringArray(writer, strings);
                return;
            case int[] ints:
                WriteFixedArray(writer, ints, AmqpFormatCode.Int, 4, static (span, item) => BinaryPrimitives.WriteInt32BigEndian(span, item));
                return;
            case uint[] uints:
                WriteFixedArray(writer, uints, AmqpFormatCode.UInt, 4, static (span, item) => BinaryPrimitives.WriteUInt32BigEndian(span, item));
                return;
            case long[] longs:
                WriteFixedArray(writer, longs, AmqpFormatCode.Long, 8, static (span, item) => BinaryPrimitives.WriteInt64BigEndian(span, item));
                return;
            case ulong[] ulongs:
                WriteFixedArray(writer, ulongs, AmqpFormatCode.ULong, 8, static (span, item) => BinaryPrimitives.WriteUInt64BigEndian(span, item));
                return;
            case short[] shorts:
                WriteFixedArray(writer, shorts, AmqpFormatCode.Short, 2, static (span, item) => BinaryPrimitives.WriteInt16BigEndian(span, item));
                return;
            case ushort[] ushorts:
                WriteFixedArray(writer, ushorts, AmqpFormatCode.UShort, 2, static (span, item) => BinaryPrimitives.WriteUInt16BigEndian(span, item));
                return;
            case sbyte[] sbytes:
                WriteFixedArray(writer, sbytes, AmqpFormatCode.Byte, 1, static (span, item) => span[0] = (byte)item);
                return;
            case bool[] booleans:
                WriteFixedArray(writer, booleans, AmqpFormatCode.Boolean, 1, static (span, item) => span[0] = item ? (byte)1 : (byte)0);
                return;
            case float[] singles:
                WriteFixedArray(writer, singles, AmqpFormatCode.Float, 4, static (span, item) => BinaryPrimitives.WriteSingleBigEndian(span, item));
                return;
            case double[] doubles:
                WriteFixedArray(writer, doubles, AmqpFormatCode.Double, 8, static (span, item) => BinaryPrimitives.WriteDoubleBigEndian(span, item));
                return;
            case Guid[] uuids:
                WriteFixedArray(writer, uuids, AmqpFormatCode.Uuid, 16, static (span, item) => item.TryWriteBytes(span, bigEndian: true, out _));
                return;
            case DateTimeOffset[] timestamps:
                WriteFixedArray(writer, timestamps, AmqpFormatCode.Timestamp, 8, static (span, item) => BinaryPrimitives.WriteInt64BigEndian(span, item.ToUnixTimeMilliseconds()));
                return;
            case IReadOnlyDictionary<AmqpSymbol, object?> symbolMap:
                WriteSymbolMap(writer, symbolMap, depth + 1);
                return;
            case IReadOnlyDictionary<string, object?> stringMap:
                WriteStringMap(writer, stringMap, depth + 1);
                return;
            case IDictionary dictionary:
                WriteDictionary(writer, dictionary, depth + 1);
                return;
            case IReadOnlyDictionary<object, object?> objectMap:
                WriteObjectMap(writer, objectMap, depth + 1);
                return;
            case IReadOnlyList<object?> list:
                WriteList(writer, list, depth + 1);
                return;
            case IList list:
                WriteNonGenericList(writer, list, depth + 1);
                return;
        }

        throw new ArgumentException(
            string.Format(CultureInfo.CurrentCulture, ErrorMessages.ValueTypeUnsupported, value.GetType()),
            nameof(value));
    }

    /// <summary>
    /// Begins a compound value (a list, map or array) by writing its 32-bit constructor and reserving its
    /// size and count, which <see cref="EndCompound"/> fills in.
    /// </summary>
    /// <returns>The position of the constructor.</returns>
    public static int BeginCompound(AmqpBufferWriter writer, byte constructor32)
    {
        int start = writer.Length;

        writer.Reserve(compoundHeaderSize)[0] = constructor32;
        return start;
    }

    /// <summary>
    /// Ends a list begun with <see cref="BeginCompound"/>, encoding an empty list as <c>list0</c>.
    /// </summary>
    public static void EndList(AmqpBufferWriter writer, int start, int count)
    {
        if (count == 0)
        {
            writer.Truncate(start);
            writer.WriteByte(AmqpFormatCode.List0);
            return;
        }

        EndCompound(writer, start, count, AmqpFormatCode.List8);
    }

    /// <summary>
    /// Ends a compound value begun with <see cref="BeginCompound"/>: fills in its size and count, and
    /// rewrites it in its 8-bit form when it is small enough.
    /// </summary>
    /// <param name="writer">The buffer.</param>
    /// <param name="start">The position <see cref="BeginCompound"/> returned.</param>
    /// <param name="count">The number of elements: for a map, keys and values both count.</param>
    /// <param name="constructor8">The 8-bit form's constructor.</param>
    public static void EndCompound(AmqpBufferWriter writer, int start, int count, byte constructor8)
    {
        int elements = writer.Length - (start + compoundHeaderSize);

        // The size counts the octets of the count and the elements.
        if (elements < byte.MaxValue && count <= byte.MaxValue)
        {
            writer.ShiftDown(start + compoundHeaderSize, start + 3);

            Span<byte> header = writer.GetWrittenSpan(start, 3);

            header[0] = constructor8;
            header[1] = (byte)(elements + 1);
            header[2] = (byte)count;
        }
        else
        {
            Span<byte> header = writer.GetWrittenSpan(start, compoundHeaderSize);

            BinaryPrimitives.WriteUInt32BigEndian(header.Slice(1), checked((uint)(elements + 4)));
            BinaryPrimitives.WriteUInt32BigEndian(header.Slice(5), (uint)count);
        }
    }

    internal static void WriteSymbolMap(AmqpBufferWriter writer, IReadOnlyDictionary<AmqpSymbol, object?>? map, int depth)
    {
        if (map is null)
        {
            WriteNull(writer);
            return;
        }

        int start = BeginCompound(writer, AmqpFormatCode.Map32);

        foreach (KeyValuePair<AmqpSymbol, object?> entry in map)
        {
            WriteSymbol(writer, entry.Key);
            WriteValue(writer, entry.Value, depth + 1);
        }

        EndCompound(writer, start, map.Count * 2, AmqpFormatCode.Map8);
    }

    private static void WriteStringMap(AmqpBufferWriter writer, IReadOnlyDictionary<string, object?> map, int depth)
    {
        int start = BeginCompound(writer, AmqpFormatCode.Map32);

        foreach (KeyValuePair<string, object?> entry in map)
        {
            WriteString(writer, entry.Key);
            WriteValue(writer, entry.Value, depth + 1);
        }

        EndCompound(writer, start, map.Count * 2, AmqpFormatCode.Map8);
    }

    private static void WriteObjectMap(AmqpBufferWriter writer, IReadOnlyDictionary<object, object?> map, int depth)
    {
        int start = BeginCompound(writer, AmqpFormatCode.Map32);

        foreach (KeyValuePair<object, object?> entry in map)
        {
            WriteValue(writer, entry.Key, depth + 1);
            WriteValue(writer, entry.Value, depth + 1);
        }

        EndCompound(writer, start, map.Count * 2, AmqpFormatCode.Map8);
    }

    private static void WriteDictionary(AmqpBufferWriter writer, IDictionary map, int depth)
    {
        int start = BeginCompound(writer, AmqpFormatCode.Map32);
        int count = 0;
        IDictionaryEnumerator entries = map.GetEnumerator();

        while (entries.MoveNext())
        {
            WriteValue(writer, entries.Key, depth + 1);
            WriteValue(writer, entries.Value, depth + 1);
            count += 2;
        }

        EndCompound(writer, start, count, AmqpFormatCode.Map8);
    }

    private static void WriteList(AmqpBufferWriter writer, IReadOnlyList<object?>? list, int depth)
    {
        if (list is null)
        {
            WriteNull(writer);
            return;
        }

        int start = BeginCompound(writer, AmqpFormatCode.List32);

        for (int i = 0; i < list.Count; i++)
        {
            WriteValue(writer, list[i], depth + 1);
        }

        EndList(writer, start, list.Count);
    }

    private static void WriteNonGenericList(AmqpBufferWriter writer, IList list, int depth)
    {
        int start = BeginCompound(writer, AmqpFormatCode.List32);

        for (int i = 0; i < list.Count; i++)
        {
            WriteValue(writer, list[i], depth + 1);
        }

        EndList(writer, start, list.Count);
    }

    private static void WriteDescribed(AmqpBufferWriter writer, AmqpDescribedValue described, int depth)
    {
        writer.WriteByte(AmqpFormatCode.Described);

        if (described.Descriptor is ulong code)
        {
            WriteULong(writer, code);
        }
        else
        {
            WriteSymbol(writer, (AmqpSymbol)described.Descriptor);
        }

        WriteValue(writer, described.Value, depth);
    }

    private static void WriteStringArray(AmqpBufferWriter writer, string[] values)
    {
        int start = BeginCompound(writer, AmqpFormatCode.Array32);

        // Every element shares the constructor, so a single long string widens all of them.
        bool narrow = true;

        foreach (string value in values)
        {
            if (Encoding.UTF8.GetByteCount(value ?? string.Empty) > byte.MaxValue)
            {
                narrow = false;
                break;
            }
        }

        writer.WriteByte(narrow ? AmqpFormatCode.String8 : AmqpFormatCode.String32);

        foreach (string value in values)
        {
            string text = value ?? string.Empty;
            int length = Encoding.UTF8.GetByteCount(text);

            if (narrow)
            {
                writer.WriteByte((byte)length);
            }
            else
            {
                writer.WriteUInt32((uint)length);
            }

            Encoding.UTF8.GetBytes(text, writer.Reserve(length));
        }

        EndCompound(writer, start, values.Length, AmqpFormatCode.Array8);
    }

    private static void WriteFixedArray<T>(AmqpBufferWriter writer, T[] values, byte constructor, int width, SpanWriter<T> write)
    {
        int start = BeginCompound(writer, AmqpFormatCode.Array32);

        writer.WriteByte(constructor);

        foreach (T value in values)
        {
            write(writer.Reserve(width), value);
        }

        EndCompound(writer, start, values.Length, AmqpFormatCode.Array8);
    }

    private static void WriteVariableHeader(AmqpBufferWriter writer, int length, byte constructor8, byte constructor32)
    {
        if (length <= byte.MaxValue)
        {
            Span<byte> span = writer.Reserve(2);
            span[0] = constructor8;
            span[1] = (byte)length;
        }
        else
        {
            Span<byte> span = writer.Reserve(5);
            span[0] = constructor32;
            BinaryPrimitives.WriteUInt32BigEndian(span.Slice(1), (uint)length);
        }
    }

    private static void ThrowIfTooDeep(int depth)
    {
        if (depth >= MaxNestingDepth)
        {
            throw new ArgumentException(ErrorMessages.ValueNestingTooDeep, "value");
        }
    }

    private delegate void SpanWriter<T>(Span<byte> destination, T value);
}

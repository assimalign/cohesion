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

    // UTF-8 that throws on half of a surrogate pair instead of writing U+FFFD, so that no string changes
    // on its way to the peer (AMQP 1.0 Part 1, section 1.6.20).
    private static readonly UTF8Encoding _utf8 = new(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true);

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
        WriteTimestamp(writer, ToTimestamp(value));
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

    /// <summary>
    /// Writes a string, or <c>null</c>.
    /// </summary>
    /// <exception cref="ArgumentException">Thrown when the string holds half of a surrogate pair, which has no UTF-8 encoding.</exception>
    public static void WriteString(AmqpBufferWriter writer, string? value)
    {
        if (value is null)
        {
            WriteNull(writer);
            return;
        }

        int length = GetUtf8Length(value);

        WriteVariableHeader(writer, length, AmqpFormatCode.String8, AmqpFormatCode.String32);
        _utf8.GetBytes(value, writer.Reserve(length));
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
    /// <exception cref="ArgumentException">Thrown when an element is a default symbol, which holds no value and so has no place in an array.</exception>
    public static void WriteSymbolArray(AmqpBufferWriter writer, IReadOnlyList<AmqpSymbol>? values)
    {
        if (values is null)
        {
            WriteNull(writer);
            return;
        }

        // Every element shares the constructor, so a single long symbol widens all of them.
        bool narrow = true;

        for (int i = 0; i < values.Count; i++)
        {
            int length = values[i].Value?.Length ?? throw ArrayElementNull(i);

            narrow &= length <= byte.MaxValue;
        }

        int start = BeginCompound(writer, AmqpFormatCode.Array32);

        writer.WriteByte(narrow ? AmqpFormatCode.Symbol8 : AmqpFormatCode.Symbol32);

        for (int i = 0; i < values.Count; i++)
        {
            string text = values[i].Value!;

            WriteElementLength(writer, text.Length, narrow);
            Encoding.ASCII.GetBytes(text, writer.Reserve(text.Length));
        }

        EndCompound(writer, start, values.Count, AmqpFormatCode.Array8);
    }

    /// <summary>
    /// Writes a map keyed by symbols, such as annotations, or <c>null</c>, at the top level of a message section.
    /// </summary>
    public static void WriteSymbolMap(AmqpBufferWriter writer, IReadOnlyDictionary<AmqpSymbol, object?>? map)
    {
        WriteSymbolMap(writer, map, 0);
    }

    /// <summary>
    /// Writes the map of an <c>application-properties</c> section, whose values are simple: no list, map or
    /// array (AMQP 1.0 Part 3, section 3.2.5), whether bare or as the value of a described type. Each value
    /// is checked as written, so the rule holds for whatever the encoder makes of it.
    /// </summary>
    /// <exception cref="ArgumentException">Thrown when a value is a list, map or array, or has no AMQP representation.</exception>
    public static void WriteApplicationProperties(AmqpBufferWriter writer, IReadOnlyDictionary<string, object?> properties)
    {
        int start = BeginCompound(writer, AmqpFormatCode.Map32);

        foreach (KeyValuePair<string, object?> property in properties)
        {
            WriteString(writer, property.Key ?? throw MapKeyMissing());

            int value = writer.Length;

            WriteValue(writer, property.Value, 1);

            if (IsCompound(writer.WrittenSpan[value..]))
            {
                throw new ArgumentException(
                    string.Format(CultureInfo.CurrentCulture, ErrorMessages.ApplicationPropertyNotSimple, property.Key, property.Value!.GetType()),
                    "message");
            }
        }

        EndCompound(writer, start, properties.Count * 2, AmqpFormatCode.Map8);
    }

    /// <summary>
    /// Writes a list of values, or <c>null</c>, at the top level of a message section.
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

    /// <summary>
    /// Writes any value the type system can represent.
    /// </summary>
    /// <param name="writer">The buffer.</param>
    /// <param name="value">The value.</param>
    /// <param name="depth">
    /// The number of compound and described values that enclose <paramref name="value"/>, counted as
    /// <see cref="AmqpReader"/> counts them, so that whatever the encoder writes the reader accepts.
    /// </param>
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
            case char character when char.IsSurrogate(character):
                throw CharSurrogate(character);
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
        }

        if (TryGetBinary(value, out ReadOnlyMemory<byte> binary))
        {
            WriteBinary(writer, binary.Span);
            return;
        }

        ThrowIfTooDeep(depth);

        if (value is Array array && TryWriteArray(writer, array))
        {
            return;
        }

        switch (value)
        {
            case AmqpArray amqpArray:
                WriteArray(writer, amqpArray, depth);
                return;
            case AmqpDescribedValue described:
                WriteDescribed(writer, described, depth);
                return;
            case AmqpError error:
                AmqpTypeCodec.WriteError(writer, error, depth);
                return;
            case AmqpDeliveryState state:
                AmqpTypeCodec.WriteDeliveryState(writer, state, depth);
                return;
            case AmqpSource source:
                AmqpTypeCodec.WriteSource(writer, source, depth);
                return;
            case AmqpTarget target:
                AmqpTypeCodec.WriteTarget(writer, target, depth);
                return;
            case AmqpCoordinator coordinator:
                AmqpTypeCodec.WriteCoordinator(writer, coordinator);
                return;
            case AmqpDeclare declare:
                AmqpTypeCodec.WriteDeclare(writer, declare, depth);
                return;
            case AmqpDischarge discharge:
                AmqpTypeCodec.WriteDischarge(writer, discharge);
                return;
            case AmqpLifetimePolicy policy:
                AmqpTypeCodec.WriteLifetimePolicy(writer, policy);
                return;
            case IReadOnlyDictionary<AmqpSymbol, object?> symbolMap:
                WriteSymbolMap(writer, symbolMap, depth);
                return;
            case IReadOnlyDictionary<string, object?> stringMap:
                WriteStringMap(writer, stringMap, depth);
                return;
            case IDictionary dictionary:
                WriteDictionary(writer, dictionary, depth);
                return;
            case IReadOnlyDictionary<object, object?> objectMap:
                WriteObjectMap(writer, objectMap, depth);
                return;
            case IReadOnlyList<object?> list:
                WriteList(writer, list, depth);
                return;
            case IList list:
                WriteNonGenericList(writer, list, depth);
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

    /// <summary>
    /// Writes a map keyed by symbols, or <c>null</c>.
    /// </summary>
    /// <param name="writer">The buffer.</param>
    /// <param name="map">The map.</param>
    /// <param name="depth">The number of compound and described values that enclose the map.</param>
    internal static void WriteSymbolMap(AmqpBufferWriter writer, IReadOnlyDictionary<AmqpSymbol, object?>? map, int depth)
    {
        if (map is null)
        {
            WriteNull(writer);
            return;
        }

        ThrowIfTooDeep(depth);

        int start = BeginCompound(writer, AmqpFormatCode.Map32);

        foreach (KeyValuePair<AmqpSymbol, object?> entry in map)
        {
            // A default symbol would encode as null, and annotations and fields hold no null key.
            if (entry.Key.Value is null)
            {
                throw MapKeyMissing();
            }

            WriteSymbol(writer, entry.Key);
            WriteValue(writer, entry.Value, depth + 1);
        }

        EndCompound(writer, start, map.Count * 2, AmqpFormatCode.Map8);
    }

    private static void WriteStringMap(AmqpBufferWriter writer, IReadOnlyDictionary<string, object?> map, int depth)
    {
        ThrowIfTooDeep(depth);

        int start = BeginCompound(writer, AmqpFormatCode.Map32);

        foreach (KeyValuePair<string, object?> entry in map)
        {
            WriteString(writer, entry.Key ?? throw MapKeyMissing());
            WriteValue(writer, entry.Value, depth + 1);
        }

        EndCompound(writer, start, map.Count * 2, AmqpFormatCode.Map8);
    }

    private static void WriteObjectMap(AmqpBufferWriter writer, IReadOnlyDictionary<object, object?> map, int depth)
    {
        ThrowIfTooDeep(depth);

        int start = BeginCompound(writer, AmqpFormatCode.Map32);

        foreach (KeyValuePair<object, object?> entry in map)
        {
            WriteMapKey(writer, entry.Key, depth + 1);
            WriteValue(writer, entry.Value, depth + 1);
        }

        EndCompound(writer, start, map.Count * 2, AmqpFormatCode.Map8);
    }

    private static void WriteDictionary(AmqpBufferWriter writer, IDictionary map, int depth)
    {
        ThrowIfTooDeep(depth);

        int start = BeginCompound(writer, AmqpFormatCode.Map32);
        int count = 0;
        IDictionaryEnumerator entries = map.GetEnumerator();

        while (entries.MoveNext())
        {
            WriteMapKey(writer, entries.Key, depth + 1);
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

        ThrowIfTooDeep(depth);

        int start = BeginCompound(writer, AmqpFormatCode.List32);

        for (int i = 0; i < list.Count; i++)
        {
            WriteValue(writer, list[i], depth + 1);
        }

        EndList(writer, start, list.Count);
    }

    private static void WriteNonGenericList(AmqpBufferWriter writer, IList list, int depth)
    {
        ThrowIfTooDeep(depth);

        int start = BeginCompound(writer, AmqpFormatCode.List32);

        for (int i = 0; i < list.Count; i++)
        {
            WriteValue(writer, list[i], depth + 1);
        }

        EndList(writer, start, list.Count);
    }

    private static void WriteDescribed(AmqpBufferWriter writer, AmqpDescribedValue described, int depth)
    {
        ThrowIfTooDeep(depth);
        WriteDescriptor(writer, described.Descriptor);

        // The reader counts a descriptor as a level of nesting, so the described value is one level deeper.
        WriteValue(writer, described.Value, depth + 1);
    }

    /// <summary>
    /// Writes the constructor of a described value and its descriptor, an <see cref="AmqpSymbol"/> or a
    /// <see cref="ulong"/>, as the model's described values and arrays hold it.
    /// </summary>
    private static void WriteDescriptor(AmqpBufferWriter writer, object descriptor)
    {
        writer.WriteByte(AmqpFormatCode.Described);

        if (descriptor is ulong code)
        {
            WriteULong(writer, code);
        }
        else
        {
            WriteSymbol(writer, (AmqpSymbol)descriptor);
        }
    }

    /// <summary>
    /// Writes an array whose element type has an AMQP array encoding.
    /// </summary>
    /// <remarks>
    /// <para>
    /// An array is written as an AMQP array when its element type maps to one AMQP type (Part 1, section
    /// 1.6.24): symbol, string, binary (a <see cref="byte"/>[][]), boolean, byte (<see cref="sbyte"/>), short,
    /// ushort, int, uint, long, ulong, float, double, decimal32, decimal64, decimal128, char (<see cref="Rune"/>
    /// or <see cref="char"/>), timestamp (<see cref="DateTimeOffset"/> or <see cref="DateTime"/>) and uuid. A
    /// <see cref="byte"/>[] is binary, never an array of ubyte. The caller writes any other array as a list.
    /// </para>
    /// <para>
    /// The runtime lets an array pass a type test for another array of the same element size and signedness
    /// rules (a <see cref="uint"/>[] is an <see cref="int"/>[], an enum[] is its underlying type's array), so
    /// the element type is matched exactly.
    /// </para>
    /// </remarks>
    /// <returns><see langword="true"/> when the array was written.</returns>
    /// <exception cref="ArgumentException">
    /// Thrown, before anything is written, when an element has no place in an AMQP array: a null string or
    /// binary, a default symbol, or a string or char with half of a surrogate pair.
    /// </exception>
    private static bool TryWriteArray(AmqpBufferWriter writer, Array array)
    {
        Type type = array.GetType();

        if (type == typeof(AmqpSymbol[]))
        {
            WriteSymbolArray(writer, (AmqpSymbol[])array);
        }
        else if (type == typeof(string[]))
        {
            WriteStringArray(writer, (string[])array);
        }
        else if (type == typeof(byte[][]))
        {
            WriteBinaryArray(writer, (byte[][])array);
        }
        else if (type == typeof(int[]))
        {
            WriteFixedArray(writer, (int[])array, AmqpFormatCode.Int, 4, static (span, item) => BinaryPrimitives.WriteInt32BigEndian(span, item));
        }
        else if (type == typeof(uint[]))
        {
            WriteFixedArray(writer, (uint[])array, AmqpFormatCode.UInt, 4, static (span, item) => BinaryPrimitives.WriteUInt32BigEndian(span, item));
        }
        else if (type == typeof(long[]))
        {
            WriteFixedArray(writer, (long[])array, AmqpFormatCode.Long, 8, static (span, item) => BinaryPrimitives.WriteInt64BigEndian(span, item));
        }
        else if (type == typeof(ulong[]))
        {
            WriteFixedArray(writer, (ulong[])array, AmqpFormatCode.ULong, 8, static (span, item) => BinaryPrimitives.WriteUInt64BigEndian(span, item));
        }
        else if (type == typeof(short[]))
        {
            WriteFixedArray(writer, (short[])array, AmqpFormatCode.Short, 2, static (span, item) => BinaryPrimitives.WriteInt16BigEndian(span, item));
        }
        else if (type == typeof(ushort[]))
        {
            WriteFixedArray(writer, (ushort[])array, AmqpFormatCode.UShort, 2, static (span, item) => BinaryPrimitives.WriteUInt16BigEndian(span, item));
        }
        else if (type == typeof(sbyte[]))
        {
            WriteFixedArray(writer, (sbyte[])array, AmqpFormatCode.Byte, 1, static (span, item) => span[0] = (byte)item);
        }
        else if (type == typeof(bool[]))
        {
            WriteFixedArray(writer, (bool[])array, AmqpFormatCode.Boolean, 1, static (span, item) => span[0] = item ? (byte)1 : (byte)0);
        }
        else if (type == typeof(float[]))
        {
            WriteFixedArray(writer, (float[])array, AmqpFormatCode.Float, 4, static (span, item) => BinaryPrimitives.WriteSingleBigEndian(span, item));
        }
        else if (type == typeof(double[]))
        {
            WriteFixedArray(writer, (double[])array, AmqpFormatCode.Double, 8, static (span, item) => BinaryPrimitives.WriteDoubleBigEndian(span, item));
        }
        else if (type == typeof(AmqpDecimal32[]))
        {
            WriteFixedArray(writer, (AmqpDecimal32[])array, AmqpFormatCode.Decimal32, 4, static (span, item) => BinaryPrimitives.WriteUInt32BigEndian(span, item.Bits));
        }
        else if (type == typeof(AmqpDecimal64[]))
        {
            WriteFixedArray(writer, (AmqpDecimal64[])array, AmqpFormatCode.Decimal64, 8, static (span, item) => BinaryPrimitives.WriteUInt64BigEndian(span, item.Bits));
        }
        else if (type == typeof(AmqpDecimal128[]))
        {
            WriteFixedArray(writer, (AmqpDecimal128[])array, AmqpFormatCode.Decimal128, 16, static (span, item) => BinaryPrimitives.WriteUInt128BigEndian(span, item.Bits));
        }
        else if (type == typeof(Rune[]))
        {
            WriteFixedArray(writer, (Rune[])array, AmqpFormatCode.Char, 4, static (span, item) => BinaryPrimitives.WriteUInt32BigEndian(span, (uint)item.Value));
        }
        else if (type == typeof(char[]))
        {
            WriteCharArray(writer, (char[])array);
        }
        else if (type == typeof(Guid[]))
        {
            WriteFixedArray(writer, (Guid[])array, AmqpFormatCode.Uuid, 16, static (span, item) => item.TryWriteBytes(span, bigEndian: true, out _));
        }
        else if (type == typeof(DateTimeOffset[]))
        {
            WriteFixedArray(writer, (DateTimeOffset[])array, AmqpFormatCode.Timestamp, 8, static (span, item) => BinaryPrimitives.WriteInt64BigEndian(span, item.ToUnixTimeMilliseconds()));
        }
        else if (type == typeof(DateTime[]))
        {
            WriteFixedArray(writer, (DateTime[])array, AmqpFormatCode.Timestamp, 8, static (span, item) => BinaryPrimitives.WriteInt64BigEndian(span, ToTimestamp(item).ToUnixTimeMilliseconds()));
        }
        else
        {
            return false;
        }

        return true;
    }

    private static void WriteStringArray(AmqpBufferWriter writer, string[] values)
    {
        // Every element shares the constructor, so a single long string widens all of them. Each element is
        // checked before the array begins, so a refused one leaves nothing written.
        bool narrow = true;

        for (int i = 0; i < values.Length; i++)
        {
            string value = values[i] ?? throw ArrayElementNull(i);

            narrow &= GetUtf8Length(value) <= byte.MaxValue;
        }

        int start = BeginCompound(writer, AmqpFormatCode.Array32);

        writer.WriteByte(narrow ? AmqpFormatCode.String8 : AmqpFormatCode.String32);

        foreach (string value in values)
        {
            int length = _utf8.GetByteCount(value);

            WriteElementLength(writer, length, narrow);
            _utf8.GetBytes(value, writer.Reserve(length));
        }

        EndCompound(writer, start, values.Length, AmqpFormatCode.Array8);
    }

    private static void WriteBinaryArray(AmqpBufferWriter writer, byte[][] values)
    {
        // As for strings, a single binary past 255 octets widens every element.
        bool narrow = true;

        for (int i = 0; i < values.Length; i++)
        {
            byte[] value = values[i] ?? throw ArrayElementNull(i);

            narrow &= value.Length <= byte.MaxValue;
        }

        int start = BeginCompound(writer, AmqpFormatCode.Array32);

        writer.WriteByte(narrow ? AmqpFormatCode.Binary8 : AmqpFormatCode.Binary32);

        foreach (byte[] value in values)
        {
            WriteElementLength(writer, value.Length, narrow);
            writer.Write(value);
        }

        EndCompound(writer, start, values.Length, AmqpFormatCode.Array8);
    }

    private static void WriteCharArray(AmqpBufferWriter writer, char[] values)
    {
        foreach (char value in values)
        {
            if (char.IsSurrogate(value))
            {
                throw CharSurrogate(value);
            }
        }

        WriteFixedArray(writer, values, AmqpFormatCode.Char, 4, static (span, item) => BinaryPrimitives.WriteUInt32BigEndian(span, item));
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

    /// <summary>
    /// Writes an <see cref="AmqpArray"/>: its elements under the one constructor of its element type, after the
    /// descriptor they share when they are described (Part 1, sections 1.6.24 and 1.2).
    /// </summary>
    /// <remarks>
    /// Every element is checked against the element type, and a variable-width one measured, before the array
    /// begins, so a refused element leaves nothing written. The elements share the constructor, so one binary,
    /// string or symbol past 255 octets widens all of them; lists, maps and arrays are written by
    /// <see cref="WriteCompoundArray"/>.
    /// </remarks>
    /// <param name="writer">The buffer.</param>
    /// <param name="array">The array.</param>
    /// <param name="depth">The number of compound and described values that enclose the array.</param>
    /// <exception cref="ArgumentException">
    /// Thrown, before anything is written, when an element is null or not a value of the element type, when an
    /// element has no place in an array (a default symbol, or a string or char with half of a surrogate pair), when
    /// an array of null holds elements, or when the array nests too deeply.
    /// </exception>
    private static void WriteArray(AmqpBufferWriter writer, AmqpArray array, int depth)
    {
        // The reader counts the descriptor of a described array as a level of nesting inside the array.
        if (array.Descriptor is not null)
        {
            ThrowIfTooDeep(depth + 1);
        }

        AmqpPrimitiveType type = array.ElementType;

        if (type is AmqpPrimitiveType.List or AmqpPrimitiveType.Map or AmqpPrimitiveType.Array)
        {
            WriteCompoundArray(writer, array, depth);
            return;
        }

        IReadOnlyList<object?> elements = array.Elements;

        // The null constructor has no width, so the reader refuses an array of null that counts any element.
        if (type == AmqpPrimitiveType.Null && elements.Count > 0)
        {
            throw new ArgumentException(ErrorMessages.ArrayOfNullNotEmpty, "value");
        }

        bool narrow = true;

        for (int i = 0; i < elements.Count; i++)
        {
            narrow &= MeasureArrayElement(type, elements[i], i) <= byte.MaxValue;
        }

        int start = BeginCompound(writer, AmqpFormatCode.Array32);

        if (array.Descriptor is object descriptor)
        {
            WriteDescriptor(writer, descriptor);
        }

        writer.WriteByte(GetElementConstructor(type, narrow));

        for (int i = 0; i < elements.Count; i++)
        {
            WriteArrayElement(writer, type, elements[i]!, narrow);
        }

        EndCompound(writer, start, elements.Count, AmqpFormatCode.Array8);
    }

    /// <summary>
    /// Writes an <see cref="AmqpArray"/> of lists, maps or arrays.
    /// </summary>
    /// <remarks>
    /// Each element is encoded on its own first, which checks it and everything it holds before the array begins,
    /// and shows whether it is a value of the element type: a value that encodes as anything else, such as a
    /// described list, is refused. The elements then share the narrowest constructor every one of them fits, the
    /// 8-bit one unless an element counts more than 255 members or takes more than 254 octets after its count.
    /// </remarks>
    private static void WriteCompoundArray(AmqpBufferWriter writer, AmqpArray array, int depth)
    {
        IReadOnlyList<object?> elements = array.Elements;
        using AmqpBufferWriter encoded = new();
        bool narrow = true;

        for (int i = 0; i < elements.Count; i++)
        {
            object element = elements[i] ?? throw ArrayElementNull(i);
            int start = encoded.Length;

            // The reader reads an element of the array one level deeper than the array.
            WriteValue(encoded, element, depth + 1);

            ReadOnlySpan<byte> value = encoded.WrittenSpan.Slice(start);

            if (!IsEncodedAs(value[0], array.ElementType))
            {
                throw ArrayElementTypeMismatch(i, element, array.ElementType);
            }

            SplitCompound(value, out uint count, out ReadOnlySpan<byte> body);
            narrow &= count <= byte.MaxValue && body.Length < byte.MaxValue;
        }

        int arrayStart = BeginCompound(writer, AmqpFormatCode.Array32);

        if (array.Descriptor is object descriptor)
        {
            WriteDescriptor(writer, descriptor);
        }

        writer.WriteByte(GetElementConstructor(array.ElementType, narrow));

        ReadOnlySpan<byte> remaining = encoded.WrittenSpan;

        while (!remaining.IsEmpty)
        {
            int length = SplitCompound(remaining, out uint count, out ReadOnlySpan<byte> body);

            // An element keeps its count and body under the shared constructor's width; its own constructor goes.
            if (narrow)
            {
                writer.WriteByte((byte)(body.Length + 1));
                writer.WriteByte((byte)count);
            }
            else
            {
                writer.WriteUInt32(checked((uint)(body.Length + 4)));
                writer.WriteUInt32(count);
            }

            writer.Write(body);
            remaining = remaining.Slice(length);
        }

        EndCompound(writer, arrayStart, elements.Count, AmqpFormatCode.Array8);
    }

    /// <summary>
    /// Splits an encoded list, map or array into its count and its body, the octets after the count, and returns
    /// the length of the whole encoding.
    /// </summary>
    private static int SplitCompound(ReadOnlySpan<byte> encoded, out uint count, out ReadOnlySpan<byte> body)
    {
        switch (encoded[0])
        {
            case AmqpFormatCode.List0:
                count = 0;
                body = default;
                return 1;
            case AmqpFormatCode.List8:
            case AmqpFormatCode.Map8:
            case AmqpFormatCode.Array8:
                int size8 = encoded[1];

                count = encoded[2];
                body = encoded.Slice(3, size8 - 1);
                return 2 + size8;
            default:
                int size32 = (int)BinaryPrimitives.ReadUInt32BigEndian(encoded.Slice(1));

                count = BinaryPrimitives.ReadUInt32BigEndian(encoded.Slice(5));
                body = encoded.Slice(9, size32 - 4);
                return 5 + size32;
        }
    }

    /// <summary>
    /// Reports whether an encoding's constructor is one of a compound element type's: a list, map or array.
    /// </summary>
    private static bool IsEncodedAs(byte constructor, AmqpPrimitiveType type)
    {
        return type switch
        {
            AmqpPrimitiveType.List => constructor is AmqpFormatCode.List0 or AmqpFormatCode.List8 or AmqpFormatCode.List32,
            AmqpPrimitiveType.Map => constructor is AmqpFormatCode.Map8 or AmqpFormatCode.Map32,
            _ => constructor is AmqpFormatCode.Array8 or AmqpFormatCode.Array32
        };
    }

    /// <summary>
    /// Checks that an element is a value of an array's element type, and returns its length in octets when the
    /// type is binary, string or symbol, whose elements carry their length, or zero.
    /// </summary>
    /// <exception cref="ArgumentException">
    /// Thrown when the element is null, a default symbol, a string or char with half of a surrogate pair, or not a
    /// value of the element type.
    /// </exception>
    private static int MeasureArrayElement(AmqpPrimitiveType type, object? element, int index)
    {
        // A boxed value keeps its exact type, so these tests do not let an int pass for a uint.
        switch (type)
        {
            case AmqpPrimitiveType.Boolean when element is bool:
            case AmqpPrimitiveType.UByte when element is byte:
            case AmqpPrimitiveType.UShort when element is ushort:
            case AmqpPrimitiveType.UInt when element is uint:
            case AmqpPrimitiveType.ULong when element is ulong:
            case AmqpPrimitiveType.Byte when element is sbyte:
            case AmqpPrimitiveType.Short when element is short:
            case AmqpPrimitiveType.Int when element is int:
            case AmqpPrimitiveType.Long when element is long:
            case AmqpPrimitiveType.Float when element is float:
            case AmqpPrimitiveType.Double when element is double:
            case AmqpPrimitiveType.Decimal32 when element is AmqpDecimal32:
            case AmqpPrimitiveType.Decimal64 when element is AmqpDecimal64:
            case AmqpPrimitiveType.Decimal128 when element is AmqpDecimal128:
            case AmqpPrimitiveType.Char when element is Rune:
            case AmqpPrimitiveType.Timestamp when element is DateTimeOffset or DateTime:
            case AmqpPrimitiveType.Uuid when element is Guid:
                return 0;
            case AmqpPrimitiveType.Char when element is char character:
                return char.IsSurrogate(character) ? throw CharSurrogate(character) : 0;
            case AmqpPrimitiveType.String when element is string text:
                return GetUtf8Length(text);
            case AmqpPrimitiveType.Symbol when element is AmqpSymbol symbol:
                return symbol.Value?.Length ?? throw ArrayElementNull(index);
            case AmqpPrimitiveType.Binary when TryGetBinary(element, out ReadOnlyMemory<byte> bytes):
                return bytes.Length;
        }

        throw element is null ? ArrayElementNull(index) : ArrayElementTypeMismatch(index, element, type);
    }

    /// <summary>
    /// Writes an element of an array, which <see cref="MeasureArrayElement"/> has checked, without its constructor.
    /// </summary>
    private static void WriteArrayElement(AmqpBufferWriter writer, AmqpPrimitiveType type, object element, bool narrow)
    {
        switch (type)
        {
            case AmqpPrimitiveType.Boolean:
                writer.WriteByte((bool)element ? (byte)1 : (byte)0);
                break;
            case AmqpPrimitiveType.UByte:
                writer.WriteByte((byte)element);
                break;
            case AmqpPrimitiveType.UShort:
                writer.WriteUInt16((ushort)element);
                break;
            case AmqpPrimitiveType.UInt:
                writer.WriteUInt32((uint)element);
                break;
            case AmqpPrimitiveType.ULong:
                writer.WriteUInt64((ulong)element);
                break;
            case AmqpPrimitiveType.Byte:
                writer.WriteByte((byte)(sbyte)element);
                break;
            case AmqpPrimitiveType.Short:
                BinaryPrimitives.WriteInt16BigEndian(writer.Reserve(2), (short)element);
                break;
            case AmqpPrimitiveType.Int:
                BinaryPrimitives.WriteInt32BigEndian(writer.Reserve(4), (int)element);
                break;
            case AmqpPrimitiveType.Long:
                BinaryPrimitives.WriteInt64BigEndian(writer.Reserve(8), (long)element);
                break;
            case AmqpPrimitiveType.Float:
                BinaryPrimitives.WriteSingleBigEndian(writer.Reserve(4), (float)element);
                break;
            case AmqpPrimitiveType.Double:
                BinaryPrimitives.WriteDoubleBigEndian(writer.Reserve(8), (double)element);
                break;
            case AmqpPrimitiveType.Decimal32:
                writer.WriteUInt32(((AmqpDecimal32)element).Bits);
                break;
            case AmqpPrimitiveType.Decimal64:
                writer.WriteUInt64(((AmqpDecimal64)element).Bits);
                break;
            case AmqpPrimitiveType.Decimal128:
                BinaryPrimitives.WriteUInt128BigEndian(writer.Reserve(16), ((AmqpDecimal128)element).Bits);
                break;
            case AmqpPrimitiveType.Char:
                writer.WriteUInt32(element is Rune rune ? (uint)rune.Value : (char)element);
                break;
            case AmqpPrimitiveType.Timestamp:
                DateTimeOffset timestamp = element is DateTime dateTime ? ToTimestamp(dateTime) : (DateTimeOffset)element;

                BinaryPrimitives.WriteInt64BigEndian(writer.Reserve(8), timestamp.ToUnixTimeMilliseconds());
                break;
            case AmqpPrimitiveType.Uuid:
                ((Guid)element).TryWriteBytes(writer.Reserve(16), bigEndian: true, out _);
                break;
            case AmqpPrimitiveType.Binary:
                TryGetBinary(element, out ReadOnlyMemory<byte> bytes);
                WriteElementLength(writer, bytes.Length, narrow);
                writer.Write(bytes.Span);
                break;
            case AmqpPrimitiveType.String:
                string text = (string)element;
                int length = _utf8.GetByteCount(text);

                WriteElementLength(writer, length, narrow);
                _utf8.GetBytes(text, writer.Reserve(length));
                break;
            case AmqpPrimitiveType.Symbol:
                // A symbol is seven-bit ASCII, so its length in octets is its length in characters.
                string name = ((AmqpSymbol)element).Value!;

                WriteElementLength(writer, name.Length, narrow);
                Encoding.ASCII.GetBytes(name, writer.Reserve(name.Length));
                break;
        }
    }

    /// <summary>
    /// Gets the constructor an array writes once for all of its elements: the 8-bit form of a variable-width or
    /// compound type when <paramref name="narrow"/> is <see langword="true"/>, and the 32-bit form otherwise.
    /// </summary>
    private static byte GetElementConstructor(AmqpPrimitiveType type, bool narrow)
    {
        return type switch
        {
            AmqpPrimitiveType.Null => AmqpFormatCode.Null,
            AmqpPrimitiveType.Boolean => AmqpFormatCode.Boolean,
            AmqpPrimitiveType.UByte => AmqpFormatCode.UByte,
            AmqpPrimitiveType.UShort => AmqpFormatCode.UShort,
            AmqpPrimitiveType.UInt => AmqpFormatCode.UInt,
            AmqpPrimitiveType.ULong => AmqpFormatCode.ULong,
            AmqpPrimitiveType.Byte => AmqpFormatCode.Byte,
            AmqpPrimitiveType.Short => AmqpFormatCode.Short,
            AmqpPrimitiveType.Int => AmqpFormatCode.Int,
            AmqpPrimitiveType.Long => AmqpFormatCode.Long,
            AmqpPrimitiveType.Float => AmqpFormatCode.Float,
            AmqpPrimitiveType.Double => AmqpFormatCode.Double,
            AmqpPrimitiveType.Decimal32 => AmqpFormatCode.Decimal32,
            AmqpPrimitiveType.Decimal64 => AmqpFormatCode.Decimal64,
            AmqpPrimitiveType.Decimal128 => AmqpFormatCode.Decimal128,
            AmqpPrimitiveType.Char => AmqpFormatCode.Char,
            AmqpPrimitiveType.Timestamp => AmqpFormatCode.Timestamp,
            AmqpPrimitiveType.Uuid => AmqpFormatCode.Uuid,
            AmqpPrimitiveType.Binary => narrow ? AmqpFormatCode.Binary8 : AmqpFormatCode.Binary32,
            AmqpPrimitiveType.String => narrow ? AmqpFormatCode.String8 : AmqpFormatCode.String32,
            AmqpPrimitiveType.Symbol => narrow ? AmqpFormatCode.Symbol8 : AmqpFormatCode.Symbol32,
            AmqpPrimitiveType.List => narrow ? AmqpFormatCode.List8 : AmqpFormatCode.List32,
            AmqpPrimitiveType.Map => narrow ? AmqpFormatCode.Map8 : AmqpFormatCode.Map32,
            _ => narrow ? AmqpFormatCode.Array8 : AmqpFormatCode.Array32
        };
    }

    /// <summary>
    /// Gets the octets of a value the encoder writes as binary.
    /// </summary>
    private static bool TryGetBinary(object? value, out ReadOnlyMemory<byte> bytes)
    {
        switch (value)
        {
            // The runtime lets an sbyte[] pass for a byte[], so binary is matched by exact type.
            case byte[] array when array.GetType() == typeof(byte[]):
                bytes = array;
                return true;
            case ReadOnlyMemory<byte> readOnlyMemory:
                bytes = readOnlyMemory;
                return true;
            case Memory<byte> memory:
                bytes = memory;
                return true;
            case ArraySegment<byte> segment:
                bytes = segment;
                return true;
            case AmqpDeliveryTag deliveryTag:
                bytes = deliveryTag.Value;
                return true;
            default:
                bytes = default;
                return false;
        }
    }

    private static void WriteElementLength(AmqpBufferWriter writer, int length, bool narrow)
    {
        if (narrow)
        {
            writer.WriteByte((byte)length);
        }
        else
        {
            writer.WriteUInt32((uint)length);
        }
    }

    /// <summary>
    /// Measures a string in UTF-8, refusing one that holds half of a surrogate pair: UTF-8 has no encoding for
    /// it, and the replacement character a lenient encoder writes would change the string in transit.
    /// </summary>
    private static int GetUtf8Length(string value)
    {
        try
        {
            return _utf8.GetByteCount(value);
        }
        catch (EncoderFallbackException exception)
        {
            throw new ArgumentException(
                string.Format(CultureInfo.CurrentCulture, ErrorMessages.StringSurrogateUnpaired, exception.Index),
                nameof(value),
                exception);
        }
    }

    // A timestamp counts milliseconds of UTC, so a local time is converted and an unspecified one taken as UTC.
    private static DateTimeOffset ToTimestamp(DateTime value)
    {
        DateTime utc = value.Kind switch
        {
            DateTimeKind.Local => value.ToUniversalTime(),
            DateTimeKind.Unspecified => DateTime.SpecifyKind(value, DateTimeKind.Utc),
            _ => value
        };

        return new DateTimeOffset(utc);
    }

    /// <summary>
    /// Writes a key of a map whose keys may be of any type, refusing one that encodes as null, such as a
    /// default symbol: the reader refuses a map with a null key, so the encoder never writes one.
    /// </summary>
    private static void WriteMapKey(AmqpBufferWriter writer, object? key, int depth)
    {
        int start = writer.Length;

        WriteValue(writer, key, depth);

        if (writer.WrittenSpan[start] == AmqpFormatCode.Null)
        {
            throw MapKeyMissing();
        }
    }

    private static ArgumentException MapKeyMissing()
    {
        return new ArgumentException(ErrorMessages.MapKeyMissing, "value");
    }

    private static ArgumentException ArrayElementNull(int index)
    {
        return new ArgumentException(string.Format(CultureInfo.CurrentCulture, ErrorMessages.ArrayElementNull, index), "value");
    }

    private static ArgumentException ArrayElementTypeMismatch(int index, object element, AmqpPrimitiveType type)
    {
        return new ArgumentException(
            string.Format(CultureInfo.CurrentCulture, ErrorMessages.ArrayElementTypeMismatch, index, element.GetType(), type),
            "value");
    }

    private static ArgumentException CharSurrogate(char value)
    {
        return new ArgumentException(string.Format(CultureInfo.CurrentCulture, ErrorMessages.CharSurrogate, (int)value), nameof(value));
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

    /// <summary>
    /// Reads past the descriptors of described values to the value they describe, and reports whether it
    /// is a list, map or array.
    /// </summary>
    private static bool IsCompound(ReadOnlySpan<byte> encoded)
    {
        AmqpReader reader = new(encoded);

        while (reader.TryReadDescriptor(out _, out _))
        {
        }

        return reader.PeekFormatCode() is AmqpFormatCode.List0
            or AmqpFormatCode.List8
            or AmqpFormatCode.List32
            or AmqpFormatCode.Map8
            or AmqpFormatCode.Map32
            or AmqpFormatCode.Array8
            or AmqpFormatCode.Array32;
    }

    internal static void ThrowIfTooDeep(int depth)
    {
        if (depth >= MaxNestingDepth)
        {
            throw new ArgumentException(ErrorMessages.ValueNestingTooDeep, "value");
        }
    }

    private delegate void SpanWriter<T>(Span<byte> destination, T value);
}

using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.Text;

using Assimalign.Cohesion.Amqp;

namespace Assimalign.Cohesion.Amqp.Connections.Internal;

/// <summary>
/// Reads values in the AMQP 1.0 type system (Part 1, section 1.6) from a contiguous buffer.
/// </summary>
/// <remarks>
/// <para>
/// Every read validates what the bytes claim against what the buffer holds, so hostile input fails
/// with <c>amqp:decode-error</c> instead of reading out of range, recursing without bound, or
/// allocating beyond its own size: a compound value reads through a child reader confined to its
/// declared size, an element count may not exceed the octets that carry it, and nesting is capped at
/// <see cref="AmqpEncoder.MaxNestingDepth"/>.
/// </para>
/// <para>
/// <see cref="Position"/> is relative to the outermost buffer, even in a child reader, so a caller that
/// holds that buffer as memory can slice it without copying.
/// </para>
/// </remarks>
[DebuggerDisplay("Position = {Position}, Remaining = {Remaining}")]
[DebuggerTypeProxy(typeof(DebugView))]
internal ref struct AmqpReader
{
    private static readonly long _minTimestamp = DateTimeOffset.MinValue.ToUnixTimeMilliseconds();
    private static readonly long _maxTimestamp = DateTimeOffset.MaxValue.ToUnixTimeMilliseconds();

    // A hostile peer can send deeply nested values at will, so the message is read once.
    private static readonly string _nestingTooDeepMessage = ErrorMessages.ValueNestingTooDeep;

    private readonly ReadOnlySpan<byte> _buffer;
    private readonly int _baseOffset;
    private int _depth;
    private int _position;

    public AmqpReader(ReadOnlySpan<byte> buffer)
        : this(buffer, 0, 0)
    {
    }

    private AmqpReader(ReadOnlySpan<byte> buffer, int baseOffset, int depth)
    {
        _buffer = buffer;
        _baseOffset = baseOffset;
        _depth = depth;
        _position = 0;
    }

    /// <summary>
    /// Gets the position of the next byte, relative to the outermost buffer.
    /// </summary>
    public readonly int Position => _baseOffset + _position;

    /// <summary>
    /// Gets the number of bytes left to read.
    /// </summary>
    public readonly int Remaining => _buffer.Length - _position;

    /// <summary>
    /// Gets a value indicating whether every byte has been read.
    /// </summary>
    public readonly bool IsEnd => _position >= _buffer.Length;

    /// <summary>
    /// Gets the bytes left to read.
    /// </summary>
    public readonly ReadOnlySpan<byte> RemainingSpan => _buffer.Slice(_position);

    public readonly byte PeekFormatCode()
    {
        EnsureAvailable(1);
        return _buffer[_position];
    }

    /// <summary>
    /// Consumes a <c>null</c> when it is next.
    /// </summary>
    /// <returns><see langword="true"/> when a <c>null</c> was consumed.</returns>
    public bool TryReadNull()
    {
        if (_position < _buffer.Length && _buffer[_position] == AmqpFormatCode.Null)
        {
            _position++;
            return true;
        }

        return false;
    }

    /// <summary>
    /// Skips the next value, whatever its type.
    /// </summary>
    public void Skip()
    {
        byte code = ReadByte();

        if (code == AmqpFormatCode.Described)
        {
            // A descriptor is itself a value, followed by the value it describes; a chain of described
            // values nests like a compound one.
            EnsureDepth();
            _depth++;
            Skip();
            Skip();
            _depth--;
            return;
        }

        SkipBody(code);
    }

    /// <summary>
    /// Consumes the descriptor of a described value when one is next.
    /// </summary>
    /// <param name="code">The numeric descriptor, or <see langword="null"/> for a symbolic descriptor AMQP 1.0 does not define.</param>
    /// <param name="descriptor">The descriptor as encoded: a <see cref="ulong"/> or an <see cref="AmqpSymbol"/>.</param>
    /// <returns><see langword="true"/> when a described value was next.</returns>
    public bool TryReadDescriptor(out ulong? code, [NotNullWhen(true)] out object? descriptor)
    {
        if (_position < _buffer.Length && _buffer[_position] == AmqpFormatCode.Described)
        {
            _position++;
            code = ReadDescriptor(out descriptor);
            return true;
        }

        code = null;
        descriptor = null;
        return false;
    }

    public bool? ReadNullableBoolean()
    {
        byte code = ReadByte();

        return code switch
        {
            AmqpFormatCode.Null => null,
            AmqpFormatCode.BooleanTrue => true,
            AmqpFormatCode.BooleanFalse => false,
            AmqpFormatCode.Boolean => ReadBooleanByte(),
            _ => throw AmqpException.DecodeError(ErrorMessages.ValueNotBoolean)
        };
    }

    public bool ReadBoolean(bool defaultValue)
    {
        return ReadNullableBoolean() ?? defaultValue;
    }

    public byte? ReadNullableUByte()
    {
        byte code = ReadByte();

        return code switch
        {
            AmqpFormatCode.Null => null,
            AmqpFormatCode.UByte => ReadByte(),
            _ => throw AmqpException.DecodeError(ErrorMessages.ValueNotUByte)
        };
    }

    public ushort? ReadNullableUShort()
    {
        byte code = ReadByte();

        return code switch
        {
            AmqpFormatCode.Null => null,
            AmqpFormatCode.UShort => BinaryPrimitives.ReadUInt16BigEndian(ReadBytes(2)),
            _ => throw AmqpException.DecodeError(ErrorMessages.ValueNotUShort)
        };
    }

    public uint? ReadNullableUInt()
    {
        byte code = ReadByte();

        return code switch
        {
            AmqpFormatCode.Null => null,
            AmqpFormatCode.UInt0 => 0u,
            AmqpFormatCode.SmallUInt => ReadByte(),
            AmqpFormatCode.UInt => BinaryPrimitives.ReadUInt32BigEndian(ReadBytes(4)),
            _ => throw AmqpException.DecodeError(ErrorMessages.ValueNotUInt)
        };
    }

    public ulong? ReadNullableULong()
    {
        byte code = ReadByte();

        return code switch
        {
            AmqpFormatCode.Null => null,
            AmqpFormatCode.ULong0 => 0ul,
            AmqpFormatCode.SmallULong => ReadByte(),
            AmqpFormatCode.ULong => BinaryPrimitives.ReadUInt64BigEndian(ReadBytes(8)),
            _ => throw AmqpException.DecodeError(ErrorMessages.ValueNotULong)
        };
    }

    public string? ReadString()
    {
        byte code = ReadByte();

        return code switch
        {
            AmqpFormatCode.Null => null,
            AmqpFormatCode.String8 or AmqpFormatCode.String32 => ReadStringBody(code),
            _ => throw AmqpException.DecodeError(ErrorMessages.ValueNotString)
        };
    }

    /// <summary>
    /// Reads an address: a string, or a symbol some peers send in its place.
    /// </summary>
    public string? ReadAddress()
    {
        byte code = ReadByte();

        return code switch
        {
            AmqpFormatCode.Null => null,
            AmqpFormatCode.String8 or AmqpFormatCode.String32 => ReadStringBody(code),
            AmqpFormatCode.Symbol8 or AmqpFormatCode.Symbol32 => ReadSymbolBody(code).Value,
            _ => throw AmqpException.DecodeError(ErrorMessages.ValueNotString)
        };
    }

    public AmqpSymbol? ReadSymbol()
    {
        byte code = ReadByte();

        // Typed so that null is not taken through the symbol's implicit conversion from string.
        return code switch
        {
            AmqpFormatCode.Null => (AmqpSymbol?)null,
            AmqpFormatCode.Symbol8 or AmqpFormatCode.Symbol32 => ReadSymbolBody(code),
            _ => throw AmqpException.DecodeError(ErrorMessages.ValueNotSymbol)
        };
    }

    /// <summary>
    /// Reads a symbol field that may hold several values (<c>multiple="true"</c>): <c>null</c>, a single
    /// symbol, or an array of symbols.
    /// </summary>
    public AmqpSymbol[]? ReadSymbolMultiple()
    {
        byte code = ReadByte();

        switch (code)
        {
            case AmqpFormatCode.Null:
                return null;
            case AmqpFormatCode.Symbol8:
            case AmqpFormatCode.Symbol32:
                return [ReadSymbolBody(code)];
            case AmqpFormatCode.Array8:
            case AmqpFormatCode.Array32:
                AmqpReader elements = ReadArrayHeader(code, out int count, out byte elementCode, out object? descriptor);

                if (descriptor is not null || (elementCode != AmqpFormatCode.Symbol8 && elementCode != AmqpFormatCode.Symbol32))
                {
                    throw AmqpException.DecodeError(ErrorMessages.ValueNotSymbolArray);
                }

                AmqpSymbol[] symbols = new AmqpSymbol[count];

                for (int i = 0; i < symbols.Length; i++)
                {
                    symbols[i] = elements.ReadSymbolBody(elementCode);
                }

                return symbols;
            default:
                throw AmqpException.DecodeError(ErrorMessages.ValueNotSymbolArray);
        }
    }

    /// <summary>
    /// Reads a binary value.
    /// </summary>
    /// <param name="value">The bytes, which belong to the buffer being read.</param>
    /// <returns><see langword="false"/> when the value is <c>null</c>.</returns>
    public bool ReadBinary(out ReadOnlySpan<byte> value)
    {
        byte code = ReadByte();

        switch (code)
        {
            case AmqpFormatCode.Null:
                value = default;
                return false;
            case AmqpFormatCode.Binary8:
            case AmqpFormatCode.Binary32:
                value = ReadVariableBody(code);
                return true;
            default:
                throw AmqpException.DecodeError(ErrorMessages.ValueNotBinary);
        }
    }

    /// <summary>
    /// Reads a binary value as its place in the outermost buffer, so the caller can slice it without copying.
    /// </summary>
    /// <returns><see langword="false"/> when the value is <c>null</c>.</returns>
    public bool ReadBinaryRange(out int offset, out int length)
    {
        byte code = ReadByte();

        switch (code)
        {
            case AmqpFormatCode.Null:
                offset = 0;
                length = 0;
                return false;
            case AmqpFormatCode.Binary8:
            case AmqpFormatCode.Binary32:
                length = ReadLength(code == AmqpFormatCode.Binary8);
                EnsureAvailable(length);
                offset = Position;
                _position += length;
                return true;
            default:
                throw AmqpException.DecodeError(ErrorMessages.ValueNotBinary);
        }
    }

    public DateTimeOffset? ReadNullableTimestamp()
    {
        byte code = ReadByte();

        return code switch
        {
            AmqpFormatCode.Null => null,
            AmqpFormatCode.Timestamp => ReadTimestampBody(),
            _ => throw AmqpException.DecodeError(ErrorMessages.ValueNotTimestamp)
        };
    }

    /// <summary>
    /// Reads a map keyed by symbols, such as <c>fields</c> or annotations. Numeric keys, which annotations
    /// reserve for future use, are skipped.
    /// </summary>
    public Dictionary<AmqpSymbol, object?>? ReadSymbolMap()
    {
        if (TryReadNull())
        {
            return null;
        }

        AmqpReader entries = ReadMap(out int count);
        Dictionary<AmqpSymbol, object?> map = new(count / 2);

        for (int i = 0; i < count; i += 2)
        {
            byte code = entries.ReadByte();

            switch (code)
            {
                case AmqpFormatCode.Symbol8:
                case AmqpFormatCode.Symbol32:
                    if (!map.TryAdd(entries.ReadSymbolBody(code), entries.ReadValue()))
                    {
                        throw AmqpException.DecodeError(ErrorMessages.MapKeyDuplicate);
                    }

                    break;
                case AmqpFormatCode.ULong0:
                case AmqpFormatCode.SmallULong:
                case AmqpFormatCode.ULong:
                    entries.SkipBody(code);
                    entries.Skip();
                    break;
                default:
                    throw AmqpException.DecodeError(ErrorMessages.MapKeyNotSymbol);
            }
        }

        return map;
    }

    /// <summary>
    /// Reads a map keyed by strings, such as <c>application-properties</c>.
    /// </summary>
    public Dictionary<string, object?>? ReadStringMap()
    {
        if (TryReadNull())
        {
            return null;
        }

        AmqpReader entries = ReadMap(out int count);
        Dictionary<string, object?> map = new(count / 2, StringComparer.Ordinal);

        for (int i = 0; i < count; i += 2)
        {
            byte code = entries.ReadByte();

            if (code != AmqpFormatCode.String8 && code != AmqpFormatCode.String32)
            {
                throw AmqpException.DecodeError(ErrorMessages.MapKeyNotString);
            }

            if (!map.TryAdd(entries.ReadStringBody(code), entries.ReadValue()))
            {
                throw AmqpException.DecodeError(ErrorMessages.MapKeyDuplicate);
            }
        }

        return map;
    }

    /// <summary>
    /// Reads a list header and returns a reader confined to the list's elements.
    /// </summary>
    /// <param name="count">The number of elements.</param>
    /// <returns>A reader over the elements.</returns>
    public AmqpReader ReadList(out int count)
    {
        byte code = ReadByte();

        if (code == AmqpFormatCode.List0)
        {
            count = 0;
            return new AmqpReader(default, Position, _depth + 1);
        }

        if (code != AmqpFormatCode.List8 && code != AmqpFormatCode.List32)
        {
            throw AmqpException.DecodeError(ErrorMessages.ValueNotList);
        }

        return ReadCompound(code == AmqpFormatCode.List8, out count);
    }

    /// <summary>
    /// Reads any value the type system can represent: primitives as their .NET types, lists as
    /// <see cref="List{T}"/>, maps as <see cref="Dictionary{TKey, TValue}"/>, arrays as typed arrays,
    /// the described types this library models as those types, and every other described value as
    /// <see cref="AmqpDescribedValue"/>.
    /// </summary>
    public object? ReadValue()
    {
        byte code = ReadByte();

        return code == AmqpFormatCode.Described ? ReadDescribedValue() : ReadBody(code);
    }

    private object? ReadDescribedValue()
    {
        // A chain of described values nests like a compound one, so it counts toward the depth.
        EnsureDepth();
        _depth++;

        object? value = ReadDescribedValueCore();

        _depth--;
        return value;
    }

    private object? ReadDescribedValueCore()
    {
        ulong? code = ReadDescriptor(out object descriptor);

        switch (code)
        {
            case AmqpDescriptor.Error:
                return AmqpTypeCodec.ReadErrorBody(ref this);
            case AmqpDescriptor.Received:
            case AmqpDescriptor.Accepted:
            case AmqpDescriptor.Rejected:
            case AmqpDescriptor.Released:
            case AmqpDescriptor.Modified:
            case AmqpDescriptor.Declared:
            case AmqpDescriptor.TransactionalState:
                return AmqpTypeCodec.ReadDeliveryStateBody(ref this, code.GetValueOrDefault());
            case AmqpDescriptor.Source:
                return AmqpTypeCodec.ReadSourceBody(ref this);
            case AmqpDescriptor.Target:
                return AmqpTypeCodec.ReadTargetBody(ref this);
            case AmqpDescriptor.Coordinator:
                return AmqpTypeCodec.ReadCoordinatorBody(ref this);
            case AmqpDescriptor.Declare:
                return AmqpTypeCodec.ReadDeclareBody(ref this);
            case AmqpDescriptor.Discharge:
                return AmqpTypeCodec.ReadDischargeBody(ref this);
            default:
                return new AmqpDescribedValue(descriptor, ReadValue());
        }
    }

    private ulong? ReadDescriptor(out object descriptor)
    {
        byte code = ReadByte();

        switch (code)
        {
            case AmqpFormatCode.ULong0:
            case AmqpFormatCode.SmallULong:
            case AmqpFormatCode.ULong:
                ulong value = ReadULongBody(code);

                descriptor = value;
                return value;
            case AmqpFormatCode.Symbol8:
            case AmqpFormatCode.Symbol32:
                AmqpSymbol name = ReadSymbolBody(code);

                descriptor = name;
                return AmqpDescriptor.TryGetCode(name.Value, out ulong mapped) ? mapped : null;
            default:
                throw AmqpException.DecodeError(ErrorMessages.DescriptorInvalid);
        }
    }

    private object? ReadBody(byte code)
    {
        switch (code)
        {
            case AmqpFormatCode.Null:
                return null;
            case AmqpFormatCode.BooleanTrue:
                return true;
            case AmqpFormatCode.BooleanFalse:
                return false;
            case AmqpFormatCode.Boolean:
                return ReadBooleanByte();
            case AmqpFormatCode.UByte:
                return ReadByte();
            case AmqpFormatCode.UShort:
                return BinaryPrimitives.ReadUInt16BigEndian(ReadBytes(2));
            case AmqpFormatCode.UInt0:
            case AmqpFormatCode.SmallUInt:
            case AmqpFormatCode.UInt:
                return ReadUIntBody(code);
            case AmqpFormatCode.ULong0:
            case AmqpFormatCode.SmallULong:
            case AmqpFormatCode.ULong:
                return ReadULongBody(code);
            case AmqpFormatCode.Byte:
                return (sbyte)ReadByte();
            case AmqpFormatCode.Short:
                return BinaryPrimitives.ReadInt16BigEndian(ReadBytes(2));
            case AmqpFormatCode.SmallInt:
            case AmqpFormatCode.Int:
                return ReadIntBody(code);
            case AmqpFormatCode.SmallLong:
            case AmqpFormatCode.Long:
                return ReadLongBody(code);
            case AmqpFormatCode.Float:
                return BinaryPrimitives.ReadSingleBigEndian(ReadBytes(4));
            case AmqpFormatCode.Double:
                return BinaryPrimitives.ReadDoubleBigEndian(ReadBytes(8));
            case AmqpFormatCode.Decimal32:
                return new AmqpDecimal32(BinaryPrimitives.ReadUInt32BigEndian(ReadBytes(4)));
            case AmqpFormatCode.Decimal64:
                return new AmqpDecimal64(BinaryPrimitives.ReadUInt64BigEndian(ReadBytes(8)));
            case AmqpFormatCode.Decimal128:
                return new AmqpDecimal128(BinaryPrimitives.ReadUInt128BigEndian(ReadBytes(16)));
            case AmqpFormatCode.Char:
                return ReadCharBody();
            case AmqpFormatCode.Timestamp:
                return ReadTimestampBody();
            case AmqpFormatCode.Uuid:
                return new Guid(ReadBytes(16), bigEndian: true);
            case AmqpFormatCode.Binary8:
            case AmqpFormatCode.Binary32:
                return ReadVariableBody(code).ToArray();
            case AmqpFormatCode.String8:
            case AmqpFormatCode.String32:
                return ReadStringBody(code);
            case AmqpFormatCode.Symbol8:
            case AmqpFormatCode.Symbol32:
                return ReadSymbolBody(code);
            case AmqpFormatCode.List0:
                return new List<object?>();
            case AmqpFormatCode.List8:
            case AmqpFormatCode.List32:
                return ReadListBody(code);
            case AmqpFormatCode.Map8:
            case AmqpFormatCode.Map32:
                return ReadMapBody(code);
            case AmqpFormatCode.Array8:
            case AmqpFormatCode.Array32:
                return ReadArrayBody(code);
            default:
                throw UnsupportedFormatCode(code);
        }
    }

    private List<object?> ReadListBody(byte code)
    {
        AmqpReader elements = ReadCompound(code == AmqpFormatCode.List8, out int count);
        List<object?> list = new(count);

        for (int i = 0; i < count; i++)
        {
            list.Add(elements.ReadValue());
        }

        return list;
    }

    private Dictionary<object, object?> ReadMapBody(byte code)
    {
        AmqpReader entries = ReadCompound(code == AmqpFormatCode.Map8, out int count);

        if ((count & 1) != 0)
        {
            throw AmqpException.DecodeError(ErrorMessages.MapElementCountOdd);
        }

        Dictionary<object, object?> map = new(count / 2, AmqpValueComparer.Instance);

        for (int i = 0; i < count; i += 2)
        {
            object key = entries.ReadValue() ?? throw AmqpException.DecodeError(ErrorMessages.MapKeyNull);

            if (!map.TryAdd(key, entries.ReadValue()))
            {
                throw AmqpException.DecodeError(ErrorMessages.MapKeyDuplicate);
            }
        }

        return map;
    }

    private object ReadArrayBody(byte code)
    {
        AmqpReader elements = ReadArrayHeader(code, out int count, out byte elementCode, out object? descriptor);

        if (descriptor is not null)
        {
            object?[] described = new object?[count];

            for (int i = 0; i < described.Length; i++)
            {
                described[i] = new AmqpDescribedValue(descriptor, elements.ReadBody(elementCode));
            }

            return described;
        }

        switch (elementCode)
        {
            case AmqpFormatCode.Symbol8:
            case AmqpFormatCode.Symbol32:
                AmqpSymbol[] symbols = new AmqpSymbol[count];

                for (int i = 0; i < symbols.Length; i++)
                {
                    symbols[i] = elements.ReadSymbolBody(elementCode);
                }

                return symbols;
            case AmqpFormatCode.String8:
            case AmqpFormatCode.String32:
                string[] strings = new string[count];

                for (int i = 0; i < strings.Length; i++)
                {
                    strings[i] = elements.ReadStringBody(elementCode);
                }

                return strings;
            case AmqpFormatCode.SmallInt:
            case AmqpFormatCode.Int:
                int[] ints = new int[count];

                for (int i = 0; i < ints.Length; i++)
                {
                    ints[i] = elements.ReadIntBody(elementCode);
                }

                return ints;
            case AmqpFormatCode.SmallUInt:
            case AmqpFormatCode.UInt:
                uint[] uints = new uint[count];

                for (int i = 0; i < uints.Length; i++)
                {
                    uints[i] = elements.ReadUIntBody(elementCode);
                }

                return uints;
            case AmqpFormatCode.SmallLong:
            case AmqpFormatCode.Long:
                long[] longs = new long[count];

                for (int i = 0; i < longs.Length; i++)
                {
                    longs[i] = elements.ReadLongBody(elementCode);
                }

                return longs;
            case AmqpFormatCode.SmallULong:
            case AmqpFormatCode.ULong:
                ulong[] ulongs = new ulong[count];

                for (int i = 0; i < ulongs.Length; i++)
                {
                    ulongs[i] = elements.ReadULongBody(elementCode);
                }

                return ulongs;
            case AmqpFormatCode.Short:
                short[] shorts = new short[count];

                for (int i = 0; i < shorts.Length; i++)
                {
                    shorts[i] = BinaryPrimitives.ReadInt16BigEndian(elements.ReadBytes(2));
                }

                return shorts;
            case AmqpFormatCode.UShort:
                ushort[] ushorts = new ushort[count];

                for (int i = 0; i < ushorts.Length; i++)
                {
                    ushorts[i] = BinaryPrimitives.ReadUInt16BigEndian(elements.ReadBytes(2));
                }

                return ushorts;
            case AmqpFormatCode.Byte:
                sbyte[] sbytes = new sbyte[count];

                for (int i = 0; i < sbytes.Length; i++)
                {
                    sbytes[i] = (sbyte)elements.ReadByte();
                }

                return sbytes;
            case AmqpFormatCode.Boolean:
                bool[] booleans = new bool[count];

                for (int i = 0; i < booleans.Length; i++)
                {
                    booleans[i] = elements.ReadBooleanByte();
                }

                return booleans;
            case AmqpFormatCode.Float:
                float[] singles = new float[count];

                for (int i = 0; i < singles.Length; i++)
                {
                    singles[i] = BinaryPrimitives.ReadSingleBigEndian(elements.ReadBytes(4));
                }

                return singles;
            case AmqpFormatCode.Double:
                double[] doubles = new double[count];

                for (int i = 0; i < doubles.Length; i++)
                {
                    doubles[i] = BinaryPrimitives.ReadDoubleBigEndian(elements.ReadBytes(8));
                }

                return doubles;
            case AmqpFormatCode.Uuid:
                Guid[] uuids = new Guid[count];

                for (int i = 0; i < uuids.Length; i++)
                {
                    uuids[i] = new Guid(elements.ReadBytes(16), bigEndian: true);
                }

                return uuids;
            case AmqpFormatCode.Timestamp:
                DateTimeOffset[] timestamps = new DateTimeOffset[count];

                for (int i = 0; i < timestamps.Length; i++)
                {
                    timestamps[i] = elements.ReadTimestampBody();
                }

                return timestamps;
            default:
                object?[] values = new object?[count];

                for (int i = 0; i < values.Length; i++)
                {
                    values[i] = elements.ReadBody(elementCode);
                }

                return values;
        }
    }

    /// <summary>
    /// Reads a map header and returns a reader confined to the map's keys and values.
    /// </summary>
    /// <param name="count">The number of elements, keys and values both counted.</param>
    /// <returns>A reader over the keys and values.</returns>
    public AmqpReader ReadMap(out int count)
    {
        byte code = ReadByte();

        if (code != AmqpFormatCode.Map8 && code != AmqpFormatCode.Map32)
        {
            throw AmqpException.DecodeError(ErrorMessages.ValueNotMap);
        }

        AmqpReader entries = ReadCompound(code == AmqpFormatCode.Map8, out count);

        if ((count & 1) != 0)
        {
            throw AmqpException.DecodeError(ErrorMessages.MapElementCountOdd);
        }

        return entries;
    }

    private AmqpReader ReadArrayHeader(byte code, out int count, out byte elementCode, out object? descriptor)
    {
        AmqpReader elements = ReadCompound(code == AmqpFormatCode.Array8, out count);

        elementCode = elements.ReadByte();
        descriptor = null;

        if (elementCode == AmqpFormatCode.Described)
        {
            elements.EnsureDepth();
            elements.ReadDescriptor(out object elementDescriptor);
            descriptor = elementDescriptor;
            elementCode = elements.ReadByte();
        }

        // An element constructor without a width would let a few bytes claim any number of elements.
        if (count > 0 && IsZeroWidth(elementCode))
        {
            throw AmqpException.DecodeError(ErrorMessages.ArrayElementWidthInvalid);
        }

        return elements;
    }

    private AmqpReader ReadCompound(bool narrow, out int count)
    {
        EnsureDepth();

        int size = ReadLength(narrow);
        int countWidth = narrow ? 1 : 4;

        if (size < countWidth)
        {
            throw AmqpException.DecodeError(ErrorMessages.CompoundSizeInvalid);
        }

        EnsureAvailable(size);

        ReadOnlySpan<byte> countBytes = _buffer.Slice(_position, countWidth);
        uint elementCount = narrow ? countBytes[0] : BinaryPrimitives.ReadUInt32BigEndian(countBytes);
        int elementsLength = size - countWidth;

        // Every element takes at least one octet, so a count beyond the octets that carry it is a lie.
        if (elementCount > (uint)elementsLength)
        {
            throw AmqpException.DecodeError(ErrorMessages.CompoundCountInvalid);
        }

        int start = _position + countWidth;
        AmqpReader elements = new(_buffer.Slice(start, elementsLength), _baseOffset + start, _depth + 1);

        _position += size;
        count = (int)elementCount;
        return elements;
    }

    private void SkipBody(byte code)
    {
        switch (code)
        {
            case AmqpFormatCode.Null:
            case AmqpFormatCode.BooleanTrue:
            case AmqpFormatCode.BooleanFalse:
            case AmqpFormatCode.UInt0:
            case AmqpFormatCode.ULong0:
            case AmqpFormatCode.List0:
                return;
            case AmqpFormatCode.Boolean:
            case AmqpFormatCode.UByte:
            case AmqpFormatCode.Byte:
            case AmqpFormatCode.SmallUInt:
            case AmqpFormatCode.SmallULong:
            case AmqpFormatCode.SmallInt:
            case AmqpFormatCode.SmallLong:
                Advance(1);
                return;
            case AmqpFormatCode.UShort:
            case AmqpFormatCode.Short:
                Advance(2);
                return;
            case AmqpFormatCode.UInt:
            case AmqpFormatCode.Int:
            case AmqpFormatCode.Float:
            case AmqpFormatCode.Char:
            case AmqpFormatCode.Decimal32:
                Advance(4);
                return;
            case AmqpFormatCode.ULong:
            case AmqpFormatCode.Long:
            case AmqpFormatCode.Double:
            case AmqpFormatCode.Timestamp:
            case AmqpFormatCode.Decimal64:
                Advance(8);
                return;
            case AmqpFormatCode.Uuid:
            case AmqpFormatCode.Decimal128:
                Advance(16);
                return;
            case AmqpFormatCode.Binary8:
            case AmqpFormatCode.String8:
            case AmqpFormatCode.Symbol8:
            case AmqpFormatCode.List8:
            case AmqpFormatCode.Map8:
            case AmqpFormatCode.Array8:
                Advance(ReadLength(narrow: true));
                return;
            case AmqpFormatCode.Binary32:
            case AmqpFormatCode.String32:
            case AmqpFormatCode.Symbol32:
            case AmqpFormatCode.List32:
            case AmqpFormatCode.Map32:
            case AmqpFormatCode.Array32:
                Advance(ReadLength(narrow: false));
                return;
            default:
                throw UnsupportedFormatCode(code);
        }
    }

    private bool ReadBooleanByte()
    {
        return ReadByte() switch
        {
            0 => false,
            1 => true,
            _ => throw AmqpException.DecodeError(ErrorMessages.BooleanInvalid)
        };
    }

    private uint ReadUIntBody(byte code)
    {
        return code switch
        {
            AmqpFormatCode.UInt0 => 0u,
            AmqpFormatCode.SmallUInt => ReadByte(),
            _ => BinaryPrimitives.ReadUInt32BigEndian(ReadBytes(4))
        };
    }

    private ulong ReadULongBody(byte code)
    {
        return code switch
        {
            AmqpFormatCode.ULong0 => 0ul,
            AmqpFormatCode.SmallULong => ReadByte(),
            _ => BinaryPrimitives.ReadUInt64BigEndian(ReadBytes(8))
        };
    }

    private int ReadIntBody(byte code)
    {
        return code == AmqpFormatCode.SmallInt
            ? (sbyte)ReadByte()
            : BinaryPrimitives.ReadInt32BigEndian(ReadBytes(4));
    }

    private long ReadLongBody(byte code)
    {
        return code == AmqpFormatCode.SmallLong
            ? (sbyte)ReadByte()
            : BinaryPrimitives.ReadInt64BigEndian(ReadBytes(8));
    }

    private Rune ReadCharBody()
    {
        uint value = BinaryPrimitives.ReadUInt32BigEndian(ReadBytes(4));

        if (value > int.MaxValue || !Rune.IsValid((int)value))
        {
            throw AmqpException.DecodeError(ErrorMessages.CharInvalid);
        }

        return new Rune((int)value);
    }

    private DateTimeOffset ReadTimestampBody()
    {
        long milliseconds = BinaryPrimitives.ReadInt64BigEndian(ReadBytes(8));

        if (milliseconds < _minTimestamp || milliseconds > _maxTimestamp)
        {
            throw AmqpException.DecodeError(ErrorMessages.TimestampOutOfRange);
        }

        return DateTimeOffset.FromUnixTimeMilliseconds(milliseconds);
    }

    private string ReadStringBody(byte code)
    {
        return Encoding.UTF8.GetString(ReadVariableBody(code));
    }

    private AmqpSymbol ReadSymbolBody(byte code)
    {
        ReadOnlySpan<byte> bytes = ReadVariableBody(code);

        if (bytes.ContainsAnyInRange((byte)0x80, (byte)0xff))
        {
            throw AmqpException.DecodeError(ErrorMessages.SymbolNotAscii);
        }

        return new AmqpSymbol(Encoding.ASCII.GetString(bytes));
    }

    private ReadOnlySpan<byte> ReadVariableBody(byte code)
    {
        // Every variable-width constructor has its 8-bit form in the 0xa_ row and its 32-bit form in 0xb_.
        int length = ReadLength(narrow: (code & 0xf0) == 0xa0);

        return ReadBytes(length);
    }

    private int ReadLength(bool narrow)
    {
        if (narrow)
        {
            return ReadByte();
        }

        uint length = BinaryPrimitives.ReadUInt32BigEndian(ReadBytes(4));

        if (length > (uint)Remaining)
        {
            throw AmqpException.DecodeError(ErrorMessages.ValueTruncated);
        }

        return (int)length;
    }

    private byte ReadByte()
    {
        EnsureAvailable(1);
        return _buffer[_position++];
    }

    private ReadOnlySpan<byte> ReadBytes(int count)
    {
        EnsureAvailable(count);

        ReadOnlySpan<byte> bytes = _buffer.Slice(_position, count);

        _position += count;
        return bytes;
    }

    private void Advance(int count)
    {
        EnsureAvailable(count);
        _position += count;
    }

    private readonly void EnsureAvailable(int count)
    {
        if ((uint)count > (uint)(_buffer.Length - _position))
        {
            throw AmqpException.DecodeError(ErrorMessages.ValueTruncated);
        }
    }

    private readonly void EnsureDepth()
    {
        if (_depth >= AmqpEncoder.MaxNestingDepth)
        {
            throw AmqpException.DecodeError(_nestingTooDeepMessage);
        }
    }

    private static bool IsZeroWidth(byte code)
    {
        return code is AmqpFormatCode.Null
            or AmqpFormatCode.BooleanTrue
            or AmqpFormatCode.BooleanFalse
            or AmqpFormatCode.UInt0
            or AmqpFormatCode.ULong0
            or AmqpFormatCode.List0;
    }

    private static AmqpException UnsupportedFormatCode(byte code)
    {
        return AmqpException.DecodeError(string.Format(CultureInfo.CurrentCulture, ErrorMessages.FormatCodeUnsupported, code));
    }

    private sealed class DebugView
    {
        private readonly int _position;
        private readonly int _remaining;

        public DebugView(AmqpReader reader)
        {
            // A ref struct cannot be held on the heap, so the view keeps a snapshot.
            _position = reader.Position;
            _remaining = reader.Remaining;
        }

        public int Position => _position;

        public int Remaining => _remaining;
    }
}

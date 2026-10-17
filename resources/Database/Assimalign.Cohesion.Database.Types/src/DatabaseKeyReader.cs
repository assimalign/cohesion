using System;
using System.Buffers.Binary;
using System.Text;

using Assimalign.Cohesion.Database.Types.Internal;

namespace Assimalign.Cohesion.Database.Types;

/// <summary>
/// Decodes keys produced by <see cref="DatabaseKeyWriter"/> component by component.
/// Keys are self-describing: <see cref="PeekType"/> reveals the next component's
/// type before it is read.
/// </summary>
public ref struct DatabaseKeyReader
{
    private readonly ReadOnlySpan<byte> _source;
    private int _position;

    /// <summary>
    /// Initializes a reader over an encoded key.
    /// </summary>
    /// <param name="source">The encoded key bytes.</param>
    public DatabaseKeyReader(ReadOnlySpan<byte> source)
    {
        _source = source;
        _position = 0;
    }

    /// <summary>
    /// Gets a value indicating whether the reader has consumed every component.
    /// </summary>
    public readonly bool IsAtEnd => _position >= _source.Length;

    /// <summary>
    /// Consumes the next component without materializing its value. Components are
    /// self-delimiting, so a caller that does not need a component (the SQL engine's
    /// rows skip the component a dropped column left behind) walks past it without
    /// allocating its string or byte payload or parsing its digits. The component is
    /// checked as strictly as the typed read would check it: its type tag, its collation
    /// for a string, its escape sequences and terminator, and its length.
    /// </summary>
    /// <exception cref="DatabaseTypeException">
    /// The reader is at the end of the key, or the component is malformed or truncated.
    /// </exception>
    public void Skip()
    {
        var type = PeekType();
        _position++;

        switch (type)
        {
            case DatabaseType.Null:
                break;
            case DatabaseType.Boolean:
            case DatabaseType.Int8:
                TakeSpan(1);
                break;
            case DatabaseType.Int16:
                TakeSpan(sizeof(short));
                break;
            case DatabaseType.Int32:
            case DatabaseType.Float32:
            case DatabaseType.Date:
                TakeSpan(sizeof(int));
                break;
            case DatabaseType.Int64:
            case DatabaseType.Float64:
            case DatabaseType.Time:
            case DatabaseType.TimeSpan:
                TakeSpan(sizeof(long));
                break;
            case DatabaseType.DateTime:
                TakeSpan(sizeof(long) + 1);
                break;
            case DatabaseType.DateTimeOffset:
                TakeSpan(sizeof(long) + sizeof(short));
                break;
            case DatabaseType.Guid:
                TakeSpan(16);
                break;
            case DatabaseType.Decimal:
                KeyComponentEncoding.SkipDecimal(_source, ref _position);
                break;
            case DatabaseType.String:
                var collation = Collation.FromId(Take());
                KeyComponentEncoding.SkipEscaped(_source, ref _position);
                if (!collation.IsIndexBacked)
                {
                    // A legacy invariant key: the original bytes follow the sort key,
                    // length-prefixed (see ReadString).
                    int length = BinaryPrimitives.ReadInt32BigEndian(TakeSpan(sizeof(int)));
                    if (length < 0 || _position + length > _source.Length)
                    {
                        throw new DatabaseTypeException("Malformed key: invalid string payload length.");
                    }

                    _position += length;
                }

                break;
            case DatabaseType.Binary:
                KeyComponentEncoding.SkipEscaped(_source, ref _position);
                break;
            default:
                throw new DatabaseTypeException($"Malformed key: unexpected component type {type}.");
        }
    }

    /// <summary>
    /// Returns the type of the next component without consuming it.
    /// </summary>
    /// <returns>The next component's type.</returns>
    /// <exception cref="DatabaseTypeException">The reader is at the end of the key.</exception>
    public readonly DatabaseType PeekType()
    {
        if (IsAtEnd)
        {
            throw new DatabaseTypeException("The key has no further components.");
        }

        return (DatabaseType)_source[_position];
    }

    /// <summary>
    /// Consumes a null component.
    /// </summary>
    /// <returns>Always <c>null</c>.</returns>
    public object? ReadNull()
    {
        Expect(DatabaseType.Null);
        return null;
    }

    /// <summary>
    /// Consumes a boolean component.
    /// </summary>
    /// <returns>The decoded value.</returns>
    public bool ReadBoolean()
    {
        Expect(DatabaseType.Boolean);
        return Take() != 0;
    }

    /// <summary>
    /// Consumes an 8-bit signed integer component.
    /// </summary>
    /// <returns>The decoded value.</returns>
    public sbyte ReadInt8()
    {
        Expect(DatabaseType.Int8);
        return (sbyte)((sbyte)Take() ^ sbyte.MinValue);
    }

    /// <summary>
    /// Consumes a 16-bit signed integer component.
    /// </summary>
    /// <returns>The decoded value.</returns>
    public short ReadInt16()
    {
        Expect(DatabaseType.Int16);
        return (short)((short)BinaryPrimitives.ReadUInt16BigEndian(TakeSpan(sizeof(short))) ^ short.MinValue);
    }

    /// <summary>
    /// Consumes a 32-bit signed integer component.
    /// </summary>
    /// <returns>The decoded value.</returns>
    public int ReadInt32()
    {
        Expect(DatabaseType.Int32);
        return (int)BinaryPrimitives.ReadUInt32BigEndian(TakeSpan(sizeof(int))) ^ int.MinValue;
    }

    /// <summary>
    /// Consumes a 64-bit signed integer component.
    /// </summary>
    /// <returns>The decoded value.</returns>
    public long ReadInt64()
    {
        Expect(DatabaseType.Int64);
        return (long)BinaryPrimitives.ReadUInt64BigEndian(TakeSpan(sizeof(long))) ^ long.MinValue;
    }

    /// <summary>
    /// Consumes a 32-bit floating point component.
    /// </summary>
    /// <returns>The decoded value.</returns>
    public float ReadFloat32()
    {
        Expect(DatabaseType.Float32);
        return KeyComponentEncoding.UnfoldSingle(BinaryPrimitives.ReadUInt32BigEndian(TakeSpan(sizeof(uint))));
    }

    /// <summary>
    /// Consumes a 64-bit floating point component.
    /// </summary>
    /// <returns>The decoded value.</returns>
    public double ReadFloat64()
    {
        Expect(DatabaseType.Float64);
        return KeyComponentEncoding.UnfoldDouble(BinaryPrimitives.ReadUInt64BigEndian(TakeSpan(sizeof(ulong))));
    }

    /// <summary>
    /// Consumes a decimal component.
    /// </summary>
    /// <returns>The decoded value.</returns>
    public decimal ReadDecimal()
    {
        Expect(DatabaseType.Decimal);
        return KeyComponentEncoding.ReadDecimal(_source, ref _position);
    }

    /// <summary>
    /// Consumes a string component, returning its value and collation.
    /// </summary>
    /// <param name="collation">The collation the component was encoded under.</param>
    /// <returns>The canonical comparison value; folding collations do not preserve original spelling.</returns>
    public string ReadString(out Collation collation)
    {
        Expect(DatabaseType.String);
        collation = Collation.FromId(Take());

        if (collation.IsIndexBacked)
        {
            return Encoding.UTF8.GetString(KeyComponentEncoding.ReadEscaped(_source, ref _position));
        }

        // Compatibility decoding for previously written legacy invariant keys.
        // New writers reject this non-index-backed collation.
        // Skip the order-defining sort key; the original bytes follow, length-prefixed.
        KeyComponentEncoding.ReadEscaped(_source, ref _position);
        int length = BinaryPrimitives.ReadInt32BigEndian(TakeSpan(sizeof(int)));

        if (length < 0 || _position + length > _source.Length)
        {
            throw new DatabaseTypeException("Malformed key: invalid string payload length.");
        }

        string value = Encoding.UTF8.GetString(_source.Slice(_position, length));
        _position += length;
        return value;
    }

    /// <summary>
    /// Consumes a binary component.
    /// </summary>
    /// <returns>The decoded bytes.</returns>
    public byte[] ReadBinary()
    {
        Expect(DatabaseType.Binary);
        return KeyComponentEncoding.ReadEscaped(_source, ref _position);
    }

    /// <summary>
    /// Consumes a date component.
    /// </summary>
    /// <returns>The decoded value.</returns>
    public DateOnly ReadDate()
    {
        Expect(DatabaseType.Date);
        int dayNumber = (int)BinaryPrimitives.ReadUInt32BigEndian(TakeSpan(sizeof(int))) ^ int.MinValue;
        return DateOnly.FromDayNumber(dayNumber);
    }

    /// <summary>
    /// Consumes a time-of-day component.
    /// </summary>
    /// <returns>The decoded value.</returns>
    public TimeOnly ReadTime()
    {
        Expect(DatabaseType.Time);
        long ticks = (long)BinaryPrimitives.ReadUInt64BigEndian(TakeSpan(sizeof(long))) ^ long.MinValue;
        return new TimeOnly(ticks);
    }

    /// <summary>
    /// Consumes a date-time component.
    /// </summary>
    /// <returns>The decoded value.</returns>
    public DateTime ReadDateTime()
    {
        Expect(DatabaseType.DateTime);
        long ticks = (long)BinaryPrimitives.ReadUInt64BigEndian(TakeSpan(sizeof(long))) ^ long.MinValue;
        var kind = (DateTimeKind)Take();
        return new DateTime(ticks, kind);
    }

    /// <summary>
    /// Consumes a date-time-offset component.
    /// </summary>
    /// <returns>The decoded value.</returns>
    public DateTimeOffset ReadDateTimeOffset()
    {
        Expect(DatabaseType.DateTimeOffset);
        long utcTicks = (long)BinaryPrimitives.ReadUInt64BigEndian(TakeSpan(sizeof(long))) ^ long.MinValue;
        short offsetMinutes = (short)((short)BinaryPrimitives.ReadUInt16BigEndian(TakeSpan(sizeof(short))) ^ short.MinValue);
        var offset = TimeSpan.FromMinutes(offsetMinutes);
        return new DateTimeOffset(utcTicks + offset.Ticks, offset);
    }

    /// <summary>
    /// Consumes a duration component.
    /// </summary>
    /// <returns>The decoded value.</returns>
    public TimeSpan ReadTimeSpan()
    {
        Expect(DatabaseType.TimeSpan);
        long ticks = (long)BinaryPrimitives.ReadUInt64BigEndian(TakeSpan(sizeof(long))) ^ long.MinValue;
        return new TimeSpan(ticks);
    }

    /// <summary>
    /// Consumes a GUID component.
    /// </summary>
    /// <returns>The decoded value.</returns>
    public Guid ReadGuid()
    {
        Expect(DatabaseType.Guid);
        return new Guid(TakeSpan(16), bigEndian: true);
    }

    private void Expect(DatabaseType expected)
    {
        var actual = PeekType();

        if (actual != expected)
        {
            throw new DatabaseTypeException($"Expected a {expected} component but found {actual}.");
        }

        _position++;
    }

    private byte Take()
    {
        if (_position >= _source.Length)
        {
            throw new DatabaseTypeException("Malformed key: truncated component.");
        }

        return _source[_position++];
    }

    private ReadOnlySpan<byte> TakeSpan(int length)
    {
        if (_position + length > _source.Length)
        {
            throw new DatabaseTypeException("Malformed key: truncated component.");
        }

        var span = _source.Slice(_position, length);
        _position += length;
        return span;
    }
}

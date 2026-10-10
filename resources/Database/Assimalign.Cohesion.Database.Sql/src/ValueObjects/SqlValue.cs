using System;
using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

using Assimalign.Cohesion.Database.Sql.Internal;
using Assimalign.Cohesion.Database.Types;

namespace Assimalign.Cohesion.Database.Sql;

/// <summary>
/// One SQL value passed to or returned from a <see cref="SqlFunction"/>: the value ABI of the
/// engine's function family. <see langword="default"/> is SQL <c>NULL</c>.
/// </summary>
/// <remarks>
/// <para>
/// A value is a struct, so passing one allocates nothing: a reference for the text and binary
/// types, a 16-byte inline payload for every other storage type (<see cref="decimal"/>,
/// <see cref="System.Guid"/> and <see cref="System.DateTimeOffset"/> included) and its
/// <see cref="DatabaseType"/>, 32 bytes in all. The layout is internal. Each storage type has one
/// <c>From…</c> factory and one <c>As…</c> accessor, named for its CLR type; an accessor of another
/// type, or of <c>NULL</c>, throws <see cref="InvalidCastException"/>, as
/// <c>DbDataReader.GetInt64</c> does. The engine converts an argument to the type the function
/// declares before the call, so a function over <see cref="SqlType.BigInt"/> reads every argument
/// with <see cref="AsInt64"/>.
/// </para>
/// <para>
/// Equality is the value object's, not SQL comparison: two NULLs are equal, text compares
/// ordinally, binary by content, and a <see cref="System.DateTimeOffset"/> by instant and offset.
/// </para>
/// </remarks>
[StructLayout(LayoutKind.Auto)]
public readonly struct SqlValue : IEquatable<SqlValue>
{
    private static readonly object BoxedTrue = true;
    private static readonly object BoxedFalse = false;

    // The text or binary value; for a value the engine read from a row, the boxed value it read,
    // so handing it back to the row allocates nothing.
    private readonly object? _reference;

    // The inline payload: 16 bytes, room for the largest storage value (a decimal, a Guid or a
    // DateTimeOffset), every other type reinterpreted in its first bytes. Typed as decimal only
    // because decimal is 16 bytes with 8-byte alignment; it is read as a number only for NUMERIC.
    private readonly decimal _payload;
    private readonly DatabaseType _type;

    private SqlValue(DatabaseType type, object? reference, decimal payload)
    {
        _type = type;
        _reference = reference;
        _payload = payload;
    }

    /// <summary>Gets SQL <c>NULL</c>, the default value.</summary>
    public static SqlValue Null => default;

    /// <summary>Gets whether the value is SQL <c>NULL</c>.</summary>
    public bool IsNull => _type == DatabaseType.Null;

    /// <summary>Gets the value's storage type; <see cref="DatabaseType.Null"/> for SQL <c>NULL</c>.</summary>
    public DatabaseType Type => _type;

    /// <summary>Creates a <c>BOOLEAN</c> value.</summary>
    /// <param name="value">The value.</param>
    /// <returns>The SQL value.</returns>
    public static SqlValue FromBoolean(bool value) => new(DatabaseType.Boolean, null, Pack(value));

    /// <summary>Creates a <c>TINYINT</c> value.</summary>
    /// <param name="value">The value.</param>
    /// <returns>The SQL value.</returns>
    public static SqlValue FromSByte(sbyte value) => new(DatabaseType.Int8, null, Pack(value));

    /// <summary>Creates a <c>SMALLINT</c> value.</summary>
    /// <param name="value">The value.</param>
    /// <returns>The SQL value.</returns>
    public static SqlValue FromInt16(short value) => new(DatabaseType.Int16, null, Pack(value));

    /// <summary>Creates an <c>INTEGER</c> value.</summary>
    /// <param name="value">The value.</param>
    /// <returns>The SQL value.</returns>
    public static SqlValue FromInt32(int value) => new(DatabaseType.Int32, null, Pack(value));

    /// <summary>Creates a <c>BIGINT</c> value.</summary>
    /// <param name="value">The value.</param>
    /// <returns>The SQL value.</returns>
    public static SqlValue FromInt64(long value) => new(DatabaseType.Int64, null, Pack(value));

    /// <summary>Creates a <c>REAL</c> value.</summary>
    /// <param name="value">The value.</param>
    /// <returns>The SQL value.</returns>
    public static SqlValue FromSingle(float value) => new(DatabaseType.Float32, null, Pack(value));

    /// <summary>Creates a <c>DOUBLE</c> value.</summary>
    /// <param name="value">The value.</param>
    /// <returns>The SQL value.</returns>
    public static SqlValue FromDouble(double value) => new(DatabaseType.Float64, null, Pack(value));

    /// <summary>Creates a <c>NUMERIC</c> value.</summary>
    /// <param name="value">The value.</param>
    /// <returns>The SQL value.</returns>
    public static SqlValue FromDecimal(decimal value) => new(DatabaseType.Decimal, null, Pack(value));

    /// <summary>Creates a <c>TEXT</c> value; <see langword="null"/> gives SQL <c>NULL</c>.</summary>
    /// <param name="value">The value.</param>
    /// <returns>The SQL value.</returns>
    public static SqlValue FromString(string? value) => value is null ? default : new(DatabaseType.String, value, default);

    /// <summary>Creates a binary value; <see langword="null"/> gives SQL <c>NULL</c>.</summary>
    /// <param name="value">The value. The engine does not copy it, so it must not change afterwards.</param>
    /// <returns>The SQL value.</returns>
    public static SqlValue FromBinary(byte[]? value) => value is null ? default : new(DatabaseType.Binary, value, default);

    /// <summary>Creates a <c>DATE</c> value.</summary>
    /// <param name="value">The value.</param>
    /// <returns>The SQL value.</returns>
    public static SqlValue FromDate(DateOnly value) => new(DatabaseType.Date, null, Pack(value));

    /// <summary>Creates a <c>TIME</c> value.</summary>
    /// <param name="value">The value.</param>
    /// <returns>The SQL value.</returns>
    public static SqlValue FromTime(TimeOnly value) => new(DatabaseType.Time, null, Pack(value));

    /// <summary>Creates a <c>TIMESTAMP</c> value.</summary>
    /// <param name="value">The value.</param>
    /// <returns>The SQL value.</returns>
    public static SqlValue FromDateTime(DateTime value) => new(DatabaseType.DateTime, null, Pack(value));

    /// <summary>Creates a <c>TIMESTAMPTZ</c> value.</summary>
    /// <param name="value">The value.</param>
    /// <returns>The SQL value.</returns>
    public static SqlValue FromDateTimeOffset(DateTimeOffset value) => new(DatabaseType.DateTimeOffset, null, Pack(value));

    /// <summary>Creates an <c>INTERVAL</c> value.</summary>
    /// <param name="value">The value.</param>
    /// <returns>The SQL value.</returns>
    public static SqlValue FromTimeSpan(TimeSpan value) => new(DatabaseType.TimeSpan, null, Pack(value));

    /// <summary>Creates a <c>UUID</c> value.</summary>
    /// <param name="value">The value.</param>
    /// <returns>The SQL value.</returns>
    public static SqlValue FromGuid(Guid value) => new(DatabaseType.Guid, null, Pack(value));

    /// <summary>Reads a <c>BOOLEAN</c> value.</summary>
    /// <returns>The value.</returns>
    /// <exception cref="InvalidCastException">The value is NULL or of another type.</exception>
    public bool AsBoolean() => Unpack<bool>(DatabaseType.Boolean);

    /// <summary>Reads a <c>TINYINT</c> value.</summary>
    /// <returns>The value.</returns>
    /// <exception cref="InvalidCastException">The value is NULL or of another type.</exception>
    public sbyte AsSByte() => Unpack<sbyte>(DatabaseType.Int8);

    /// <summary>Reads a <c>SMALLINT</c> value.</summary>
    /// <returns>The value.</returns>
    /// <exception cref="InvalidCastException">The value is NULL or of another type.</exception>
    public short AsInt16() => Unpack<short>(DatabaseType.Int16);

    /// <summary>Reads an <c>INTEGER</c> value.</summary>
    /// <returns>The value.</returns>
    /// <exception cref="InvalidCastException">The value is NULL or of another type.</exception>
    public int AsInt32() => Unpack<int>(DatabaseType.Int32);

    /// <summary>Reads a <c>BIGINT</c> value.</summary>
    /// <returns>The value.</returns>
    /// <exception cref="InvalidCastException">The value is NULL or of another type.</exception>
    public long AsInt64() => Unpack<long>(DatabaseType.Int64);

    /// <summary>Reads a <c>REAL</c> value.</summary>
    /// <returns>The value.</returns>
    /// <exception cref="InvalidCastException">The value is NULL or of another type.</exception>
    public float AsSingle() => Unpack<float>(DatabaseType.Float32);

    /// <summary>Reads a <c>DOUBLE</c> value.</summary>
    /// <returns>The value.</returns>
    /// <exception cref="InvalidCastException">The value is NULL or of another type.</exception>
    public double AsDouble() => Unpack<double>(DatabaseType.Float64);

    /// <summary>Reads a <c>NUMERIC</c> value.</summary>
    /// <returns>The value.</returns>
    /// <exception cref="InvalidCastException">The value is NULL or of another type.</exception>
    public decimal AsDecimal() => Unpack<decimal>(DatabaseType.Decimal);

    /// <summary>Reads a <c>TEXT</c> value.</summary>
    /// <returns>The value.</returns>
    /// <exception cref="InvalidCastException">The value is NULL or of another type.</exception>
    public string AsString()
    {
        if (_type != DatabaseType.String)
        {
            ThrowMismatch(DatabaseType.String);
        }

        return Unsafe.As<string>(_reference!);
    }

    /// <summary>
    /// Reads a binary value, read-only: the bytes are the engine's (a row's stored value, or a
    /// caller's parameter), so a function cannot change them. <see cref="ReadOnlyMemory{T}.ToArray"/>
    /// copies them when the function needs an array of its own.
    /// </summary>
    /// <returns>The value.</returns>
    /// <exception cref="InvalidCastException">The value is NULL or of another type.</exception>
    public ReadOnlyMemory<byte> AsBinary()
    {
        if (_type != DatabaseType.Binary)
        {
            ThrowMismatch(DatabaseType.Binary);
        }

        return Unsafe.As<byte[]>(_reference!);
    }

    /// <summary>Reads a <c>DATE</c> value.</summary>
    /// <returns>The value.</returns>
    /// <exception cref="InvalidCastException">The value is NULL or of another type.</exception>
    public DateOnly AsDate() => Unpack<DateOnly>(DatabaseType.Date);

    /// <summary>Reads a <c>TIME</c> value.</summary>
    /// <returns>The value.</returns>
    /// <exception cref="InvalidCastException">The value is NULL or of another type.</exception>
    public TimeOnly AsTime() => Unpack<TimeOnly>(DatabaseType.Time);

    /// <summary>Reads a <c>TIMESTAMP</c> value.</summary>
    /// <returns>The value.</returns>
    /// <exception cref="InvalidCastException">The value is NULL or of another type.</exception>
    public DateTime AsDateTime() => Unpack<DateTime>(DatabaseType.DateTime);

    /// <summary>Reads a <c>TIMESTAMPTZ</c> value.</summary>
    /// <returns>The value.</returns>
    /// <exception cref="InvalidCastException">The value is NULL or of another type.</exception>
    public DateTimeOffset AsDateTimeOffset() => Unpack<DateTimeOffset>(DatabaseType.DateTimeOffset);

    /// <summary>Reads an <c>INTERVAL</c> value.</summary>
    /// <returns>The value.</returns>
    /// <exception cref="InvalidCastException">The value is NULL or of another type.</exception>
    public TimeSpan AsTimeSpan() => Unpack<TimeSpan>(DatabaseType.TimeSpan);

    /// <summary>Reads a <c>UUID</c> value.</summary>
    /// <returns>The value.</returns>
    /// <exception cref="InvalidCastException">The value is NULL or of another type.</exception>
    public Guid AsGuid() => Unpack<Guid>(DatabaseType.Guid);

    /// <inheritdoc />
    public bool Equals(SqlValue other)
    {
        if (_type != other._type)
        {
            return false;
        }

        return _type switch
        {
            DatabaseType.Null => true,
            DatabaseType.String => string.Equals((string)_reference!, (string)other._reference!, StringComparison.Ordinal),
            DatabaseType.Binary => ((byte[])_reference!).AsSpan().SequenceEqual((byte[])other._reference!),
            DatabaseType.Float32 => Read<float>().Equals(other.Read<float>()),
            DatabaseType.Float64 => Read<double>().Equals(other.Read<double>()),
            DatabaseType.Decimal => Read<decimal>() == other.Read<decimal>(),
            DatabaseType.DateTime => Read<DateTime>() == other.Read<DateTime>(),
            DatabaseType.DateTimeOffset => Read<DateTimeOffset>().EqualsExact(other.Read<DateTimeOffset>()),
            DatabaseType.Guid => Read<Guid>() == other.Read<Guid>(),
            _ => Read<long>() == other.Read<long>(),
        };
    }

    /// <inheritdoc />
    public override bool Equals(object? obj) => obj is SqlValue other && Equals(other);

    /// <inheritdoc />
    public override int GetHashCode() => _type switch
    {
        DatabaseType.Null => 0,
        DatabaseType.String => HashCode.Combine(_type, StringComparer.Ordinal.GetHashCode((string)_reference!)),
        DatabaseType.Binary => HashCode.Combine(_type, ((byte[])_reference!).Length),
        DatabaseType.Float32 => HashCode.Combine(_type, Read<float>()),
        DatabaseType.Float64 => HashCode.Combine(_type, Read<double>()),
        DatabaseType.Decimal => HashCode.Combine(_type, Read<decimal>()),
        DatabaseType.DateTime => HashCode.Combine(_type, Read<DateTime>()),
        DatabaseType.DateTimeOffset => HashCode.Combine(_type, Read<DateTimeOffset>().UtcTicks, Read<DateTimeOffset>().Offset),
        DatabaseType.Guid => HashCode.Combine(_type, Read<Guid>()),
        _ => HashCode.Combine(_type, Read<long>()),
    };

    /// <summary>Formats the value with the invariant culture; <c>NULL</c> for SQL <c>NULL</c>.</summary>
    /// <returns>The text.</returns>
    public override string ToString() => _type switch
    {
        DatabaseType.Null => "NULL",
        DatabaseType.String => (string)_reference!,
        DatabaseType.Binary => "0x" + Convert.ToHexString((byte[])_reference!),
        _ => Convert.ToString(ToObject(), CultureInfo.InvariantCulture) ?? string.Empty,
    };

    /// <summary>Whether two values are equal (<see cref="Equals(SqlValue)"/>).</summary>
    /// <param name="left">The first value.</param>
    /// <param name="right">The second value.</param>
    /// <returns><see langword="true"/> when they are equal.</returns>
    public static bool operator ==(SqlValue left, SqlValue right) => left.Equals(right);

    /// <summary>Whether two values differ (<see cref="Equals(SqlValue)"/>).</summary>
    /// <param name="left">The first value.</param>
    /// <param name="right">The second value.</param>
    /// <returns><see langword="true"/> when they differ.</returns>
    public static bool operator !=(SqlValue left, SqlValue right) => !left.Equals(right);

    /// <summary>
    /// Compares two values in the order the engine sorts and groups by and takes <c>MIN</c> and
    /// <c>MAX</c> in, which <see cref="Equals(SqlValue)"/> is not: numbers by value across the
    /// numeric types, text under a collation, binary by content, a <c>TIMESTAMP</c> by its
    /// wall-clock ticks and a <c>TIMESTAMPTZ</c> by its instant.
    /// </summary>
    /// <param name="left">The first value; not NULL.</param>
    /// <param name="right">The second value; not NULL.</param>
    /// <param name="collation">The collation text compares under; the binary collation when null.</param>
    /// <returns>Negative when <paramref name="left"/> sorts first, zero when they are equal, positive otherwise.</returns>
    /// <exception cref="ArgumentException">A value is NULL, which has no place in the order.</exception>
    /// <exception cref="DatabaseException">The values' types do not compare, such as TEXT and BIGINT.</exception>
    public static int Compare(in SqlValue left, in SqlValue right, Collation? collation = null)
    {
        var type = left._type;
        if (type == DatabaseType.Null || right._type == DatabaseType.Null)
        {
            ThrowNullCompared(left.IsNull);
        }

        if (type != right._type)
        {
            // Across types (INTEGER and NUMERIC, REAL and DOUBLE): the engine's comparer, which
            // compares numbers by value and fails a pair that does not compare.
            return SqlValueComparer.Compare(left.ToObject()!, right.ToObject()!, collation);
        }

        // Two values of one type compare on the payload, in the order the engine's comparer gives
        // them and with its result (-1, 0 or 1 for a number): no box is read back and no type is
        // tested again. SMALLINT and TINYINT widen first, since short.CompareTo and
        // sbyte.CompareTo return the difference of the two values.
        return type switch
        {
            DatabaseType.String => (collation ?? Collation.Binary).Compare(Unsafe.As<string>(left._reference!), Unsafe.As<string>(right._reference!)),
            DatabaseType.Int64 => left.Read<long>().CompareTo(right.Read<long>()),
            DatabaseType.Int32 => left.Read<int>().CompareTo(right.Read<int>()),
            DatabaseType.Decimal => left.Read<decimal>().CompareTo(right.Read<decimal>()),
            DatabaseType.Float64 => left.Read<double>().CompareTo(right.Read<double>()), // NaN first, as the comparer orders it
            DatabaseType.Int16 => ((int)left.Read<short>()).CompareTo(right.Read<short>()),
            DatabaseType.Int8 => ((int)left.Read<sbyte>()).CompareTo(right.Read<sbyte>()),
            DatabaseType.Float32 => left.Read<float>().CompareTo(right.Read<float>()),
            DatabaseType.Boolean => left.Read<bool>().CompareTo(right.Read<bool>()),
            DatabaseType.Binary => Unsafe.As<byte[]>(left._reference!).AsSpan().SequenceCompareTo(Unsafe.As<byte[]>(right._reference!)),
            DatabaseType.Date => left.Read<DateOnly>().CompareTo(right.Read<DateOnly>()),
            DatabaseType.Time => left.Read<TimeOnly>().CompareTo(right.Read<TimeOnly>()),
            DatabaseType.DateTime => left.Read<DateTime>().Ticks.CompareTo(right.Read<DateTime>().Ticks),
            DatabaseType.DateTimeOffset => left.Read<DateTimeOffset>().UtcTicks.CompareTo(right.Read<DateTimeOffset>().UtcTicks),
            DatabaseType.TimeSpan => left.Read<TimeSpan>().CompareTo(right.Read<TimeSpan>()),
            DatabaseType.Guid => left.Read<Guid>().CompareTo(right.Read<Guid>()),
            _ => SqlValueComparer.Compare(left.ToObject()!, right.ToObject()!, collation),
        };
    }

    [DoesNotReturn]
    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void ThrowNullCompared(bool leftIsNull)
        => throw new ArgumentException("NULL has no place in the order; test IsNull first.", leftIsNull ? "left" : "right");

    /// <summary>
    /// Converts a value of the engine's row representation: <see langword="null"/> or a boxed value
    /// of a storage CLR type. The box is kept, so <see cref="ToObject"/> hands the same object back
    /// without boxing again. A parameter of a CLR type the codec does not store widens losslessly:
    /// <see cref="byte"/> to SMALLINT, <see cref="ushort"/> to INTEGER, <see cref="uint"/> to BIGINT,
    /// <see cref="ulong"/> to NUMERIC and <see cref="char"/> to TEXT.
    /// </summary>
    /// <param name="value">The row value.</param>
    /// <returns>The SQL value.</returns>
    /// <exception cref="DatabaseException">The value's CLR type has no SQL type.</exception>
    /// <remarks>
    /// Every call of every function, a built-in's or an application's, converts its arguments
    /// here, so the common row types (NULL, text, BIGINT, INTEGER and NUMERIC) convert inline at
    /// the call site; the other types convert in <see cref="FromOtherObject"/>, out of line.
    /// </remarks>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal static SqlValue FromObject(object? value)
    {
        if (value is null)
        {
            return default;
        }
        if (value is string text)
        {
            return new(DatabaseType.String, text, default);
        }
        if (value is long int64)
        {
            return new(DatabaseType.Int64, value, Pack(int64));
        }
        if (value is int int32)
        {
            return new(DatabaseType.Int32, value, Pack(int32));
        }
        if (value is decimal number)
        {
            return new(DatabaseType.Decimal, value, Pack(number));
        }

        return FromOtherObject(value);
    }

    /// <summary>Converts a row value of a type <see cref="FromObject"/> does not convert inline.</summary>
    /// <param name="value">The row value; not null, text, BIGINT, INTEGER or NUMERIC.</param>
    /// <returns>The SQL value.</returns>
    /// <exception cref="DatabaseException">The value's CLR type has no SQL type.</exception>
    [MethodImpl(MethodImplOptions.NoInlining)]
    private static SqlValue FromOtherObject(object value) => value switch
    {
        double number => new(DatabaseType.Float64, value, Pack(number)),
        bool flag => new(DatabaseType.Boolean, value, Pack(flag)),
        short number => new(DatabaseType.Int16, value, Pack(number)),
        sbyte number => new(DatabaseType.Int8, value, Pack(number)),
        float number => new(DatabaseType.Float32, value, Pack(number)),
        byte[] bytes => new(DatabaseType.Binary, bytes, default),
        DateTime moment => new(DatabaseType.DateTime, value, Pack(moment)),
        DateTimeOffset moment => new(DatabaseType.DateTimeOffset, value, Pack(moment)),
        DateOnly date => new(DatabaseType.Date, value, Pack(date)),
        TimeOnly time => new(DatabaseType.Time, value, Pack(time)),
        TimeSpan span => new(DatabaseType.TimeSpan, value, Pack(span)),
        Guid id => new(DatabaseType.Guid, value, Pack(id)),
        byte number => FromInt16(number),
        ushort number => FromInt32(number),
        uint number => FromInt64(number),
        ulong number => FromDecimal(number),
        char character => FromString(character.ToString()),
        _ => throw new DatabaseException($"A value of CLR type '{value.GetType().Name}' has no SQL type a function can take."),
    };

    /// <summary>
    /// Converts the value to the engine's row representation: <see langword="null"/>, the text or
    /// binary reference, or a boxed value; a value read from a row returns the box it was read from.
    /// </summary>
    /// <returns>The row value.</returns>
    /// <remarks>
    /// Inline at the call site for a value that has its row representation already (NULL, a
    /// reference, a value read from a row), which is every argument handed back and every text
    /// result, and for a computed BIGINT, the type counts and lengths have, which boxes once; a
    /// value of another type a function computed boxes in <see cref="Box"/>, out of line.
    /// </remarks>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal object? ToObject()
    {
        if (_reference is not null || _type == DatabaseType.Null)
        {
            return _reference;
        }

        return _type == DatabaseType.Int64 ? Read<long>() : Box();
    }

    /// <summary>Boxes a value that has no reference: one a function computed, not one read from a row.</summary>
    /// <returns>The boxed value.</returns>
    [MethodImpl(MethodImplOptions.NoInlining)]
    private object Box()
    {
        return _type switch
        {
            // Shared boxes, as the evaluator's predicates return: a function returning BOOLEAN
            // allocates nothing per row.
            DatabaseType.Boolean => Read<bool>() ? BoxedTrue : BoxedFalse,
            DatabaseType.Int8 => Read<sbyte>(),
            DatabaseType.Int16 => Read<short>(),
            DatabaseType.Int32 => Read<int>(),
            DatabaseType.Int64 => Read<long>(),
            DatabaseType.Float32 => Read<float>(),
            DatabaseType.Float64 => Read<double>(),
            DatabaseType.Decimal => Read<decimal>(),
            DatabaseType.Date => Read<DateOnly>(),
            DatabaseType.Time => Read<TimeOnly>(),
            DatabaseType.DateTime => Read<DateTime>(),
            DatabaseType.DateTimeOffset => Read<DateTimeOffset>(),
            DatabaseType.TimeSpan => Read<TimeSpan>(),
            DatabaseType.Guid => Read<Guid>(),
            _ => throw new InvalidOperationException($"A {_type} value has no row representation."),
        };
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static decimal Pack<T>(T value)
        where T : unmanaged
    {
        decimal payload = default;
        Unsafe.As<decimal, T>(ref payload) = value;
        return payload;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private T Read<T>()
        where T : unmanaged
        => Unsafe.As<decimal, T>(ref Unsafe.AsRef(in _payload));

    // The throw lives in its own method, so every accessor stays small enough to inline.
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private T Unpack<T>(DatabaseType type)
        where T : unmanaged
    {
        if (_type != type)
        {
            ThrowMismatch(type);
        }

        return Read<T>();
    }

    [DoesNotReturn]
    [MethodImpl(MethodImplOptions.NoInlining)]
    private void ThrowMismatch(DatabaseType requested) => throw Mismatch(requested);

    private InvalidCastException Mismatch(DatabaseType requested) => _type == DatabaseType.Null
        ? new InvalidCastException($"The value is NULL, not {SqlType.NameOf(requested)}.")
        : new InvalidCastException($"The value is {SqlType.NameOf(_type)}, not {SqlType.NameOf(requested)}; read it with the accessor of its type.");
}

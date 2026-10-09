using System;
using System.Globalization;

using Assimalign.Cohesion.Database.Types;

namespace Assimalign.Cohesion.Database.Sql;

/// <summary>
/// A SQL type a function declares for a parameter or its result: one of the engine's storage types,
/// or a pseudo-type that stands for several (<see cref="Any"/>, <see cref="AnyElement"/>).
/// </summary>
/// <remarks>
/// <para>
/// The descriptor is open: a value is a name over the physical type the row codec stores
/// (<see cref="Storage"/>), so domains over a base type join it in phase E3 without a change to the
/// codec, the key encoding or the on-disk format. In phase E2 the instances are the built-in types
/// below, and parameterized <see cref="Decimal(int, int)"/> and <see cref="VarChar(int)"/>.
/// </para>
/// <para>
/// A function's signature identity and overload resolution compare storage types only, as
/// PostgreSQL ignores a type modifier in a function's signature: <c>VARCHAR(20)</c> and
/// <see cref="Text"/> are the same parameter type, and a call is not checked against a length,
/// precision or scale.
/// </para>
/// <para>
/// Pseudo-types never describe a stored value. <see cref="Any"/> accepts an argument of any type,
/// each independently. <see cref="AnyElement"/> accepts any type too, but every
/// <see cref="AnyElement"/> argument of one call must have the same type, and a result declared
/// <see cref="AnyElement"/> has that type (PostgreSQL's <c>anyelement</c>).
/// </para>
/// </remarks>
public sealed class SqlType : IEquatable<SqlType>
{
    private SqlType(string name, DatabaseTypeInfo storage, bool isPseudo)
    {
        Name = name;
        Storage = storage;
        IsPseudo = isPseudo;
    }

    /// <summary>Gets the type's SQL name, as a diagnostic or a signature shows it.</summary>
    public string Name { get; }

    /// <summary>
    /// Gets the physical type the row codec stores, with its length, precision and scale when the
    /// type declares them; <see cref="DatabaseType.Null"/> for a pseudo-type.
    /// </summary>
    public DatabaseTypeInfo Storage { get; }

    /// <summary>Gets whether the type is a pseudo-type, which stands for several storage types.</summary>
    public bool IsPseudo { get; }

    /// <summary>Gets <c>BOOLEAN</c>.</summary>
    public static SqlType Boolean { get; } = Create("BOOLEAN", DatabaseType.Boolean);

    /// <summary>Gets <c>TINYINT</c>, the 8-bit integer.</summary>
    public static SqlType TinyInt { get; } = Create("TINYINT", DatabaseType.Int8);

    /// <summary>Gets <c>SMALLINT</c>, the 16-bit integer.</summary>
    public static SqlType SmallInt { get; } = Create("SMALLINT", DatabaseType.Int16);

    /// <summary>Gets <c>INTEGER</c>, the 32-bit integer.</summary>
    public static SqlType Integer { get; } = Create("INTEGER", DatabaseType.Int32);

    /// <summary>Gets <c>BIGINT</c>, the 64-bit integer.</summary>
    public static SqlType BigInt { get; } = Create("BIGINT", DatabaseType.Int64);

    /// <summary>Gets <c>REAL</c>, the 32-bit binary floating point.</summary>
    public static SqlType Real { get; } = Create("REAL", DatabaseType.Float32);

    /// <summary>Gets <c>DOUBLE</c>, the 64-bit binary floating point.</summary>
    public static SqlType Double { get; } = Create("DOUBLE", DatabaseType.Float64);

    /// <summary>Gets <c>NUMERIC</c>, the exact decimal without a declared precision.</summary>
    public static SqlType Numeric { get; } = Create("NUMERIC", DatabaseType.Decimal);

    /// <summary>Gets <c>TEXT</c>, the Unicode string without a declared length.</summary>
    public static SqlType Text { get; } = Create("TEXT", DatabaseType.String);

    /// <summary>Gets <c>BINARY</c>, the byte string.</summary>
    public static SqlType Binary { get; } = Create("BINARY", DatabaseType.Binary);

    /// <summary>Gets <c>DATE</c>.</summary>
    public static SqlType Date { get; } = Create("DATE", DatabaseType.Date);

    /// <summary>Gets <c>TIME</c>.</summary>
    public static SqlType Time { get; } = Create("TIME", DatabaseType.Time);

    /// <summary>Gets <c>TIMESTAMP</c>, a date and time without an offset.</summary>
    public static SqlType Timestamp { get; } = Create("TIMESTAMP", DatabaseType.DateTime);

    /// <summary>Gets <c>TIMESTAMPTZ</c>, a date and time with its offset.</summary>
    public static SqlType TimestampTz { get; } = Create("TIMESTAMPTZ", DatabaseType.DateTimeOffset);

    /// <summary>Gets <c>INTERVAL</c>, a duration.</summary>
    public static SqlType Interval { get; } = Create("INTERVAL", DatabaseType.TimeSpan);

    /// <summary>Gets <c>UUID</c>.</summary>
    public static SqlType Uuid { get; } = Create("UUID", DatabaseType.Guid);

    /// <summary>Gets <c>JSON</c>, JSON text.</summary>
    public static SqlType Json { get; } = Create("JSON", DatabaseType.Json);

    /// <summary>Gets <c>JSONB</c>, binary JSON.</summary>
    public static SqlType Jsonb { get; } = Create("JSONB", DatabaseType.JsonBinary);

    /// <summary>Gets the pseudo-type that accepts an argument of any type, each argument independently.</summary>
    public static SqlType Any { get; } = new("ANY", new DatabaseTypeInfo(DatabaseType.Null), isPseudo: true);

    /// <summary>
    /// Gets the polymorphic pseudo-type: every <see cref="AnyElement"/> argument of a call has one
    /// type, and a result declared <see cref="AnyElement"/> has that type.
    /// </summary>
    public static SqlType AnyElement { get; } = new("ANYELEMENT", new DatabaseTypeInfo(DatabaseType.Null), isPseudo: true);

    /// <summary>
    /// The pseudo-type of the standard library's numeric aggregates (<c>SUM</c>, <c>AVG</c>): any
    /// numeric storage type, each argument independently. Internal until an application needs it.
    /// </summary>
    internal static SqlType AnyNumeric { get; } = new("ANYNUMERIC", new DatabaseTypeInfo(DatabaseType.Null), isPseudo: true);

    /// <summary>Gets the built-in storage types, in declaration order; the pseudo-types are not among them.</summary>
    internal static SqlType[] BuiltIn { get; } =
    [
        Boolean, TinyInt, SmallInt, Integer, BigInt, Real, Double, Numeric, Text, Binary,
        Date, Time, Timestamp, TimestampTz, Interval, Uuid, Json, Jsonb,
    ];

    /// <summary>Creates <c>NUMERIC(precision, scale)</c>.</summary>
    /// <param name="precision">The total number of significant digits, at least 1.</param>
    /// <param name="scale">The digits after the decimal point, 0 to <paramref name="precision"/>.</param>
    /// <returns>The type.</returns>
    /// <exception cref="ArgumentOutOfRangeException">The precision or the scale is outside its range.</exception>
    public static SqlType Decimal(int precision, int scale)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(precision, 1);
        ArgumentOutOfRangeException.ThrowIfNegative(scale);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(scale, precision);
        return new(
            string.Create(CultureInfo.InvariantCulture, $"NUMERIC({precision}, {scale})"),
            new DatabaseTypeInfo(DatabaseType.Decimal, precision: precision, scale: scale),
            isPseudo: false);
    }

    /// <summary>Creates <c>VARCHAR(maxLength)</c>.</summary>
    /// <param name="maxLength">The most characters a value holds, at least 1.</param>
    /// <returns>The type.</returns>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="maxLength"/> is less than 1.</exception>
    public static SqlType VarChar(int maxLength)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(maxLength, 1);
        return new(
            string.Create(CultureInfo.InvariantCulture, $"VARCHAR({maxLength})"),
            new DatabaseTypeInfo(DatabaseType.String, maxLength: maxLength),
            isPseudo: false);
    }

    /// <inheritdoc />
    public bool Equals(SqlType? other)
        => other is not null && (ReferenceEquals(this, other)
            || IsPseudo == other.IsPseudo
            && string.Equals(Name, other.Name, StringComparison.OrdinalIgnoreCase)
            && Storage.Type == other.Storage.Type
            && Storage.MaxLength == other.Storage.MaxLength
            && Storage.Precision == other.Storage.Precision
            && Storage.Scale == other.Storage.Scale);

    /// <inheritdoc />
    public override bool Equals(object? obj) => Equals(obj as SqlType);

    /// <inheritdoc />
    public override int GetHashCode()
        => HashCode.Combine(StringComparer.OrdinalIgnoreCase.GetHashCode(Name), Storage.Type, Storage.MaxLength, Storage.Precision, Storage.Scale);

    /// <summary>Returns the type's SQL name.</summary>
    /// <returns>The name.</returns>
    public override string ToString() => Name;

    /// <summary>
    /// Whether two types are the same parameter type for a function's signature: the same
    /// pseudo-type, or the same storage type whatever its length, precision or scale.
    /// </summary>
    /// <param name="other">The other type.</param>
    /// <returns><see langword="true"/> when a signature cannot tell them apart.</returns>
    internal bool IsSameParameterType(SqlType other)
        => IsPseudo || other.IsPseudo ? ReferenceEquals(this, other) : Storage.Type == other.Storage.Type;

    /// <summary>Names a storage type the way a diagnostic names a function's argument.</summary>
    /// <param name="type">The storage type; <see cref="DatabaseType.Null"/> for an argument whose type is not known.</param>
    /// <returns>The SQL name, for example <c>BIGINT</c>, or <c>unknown</c>.</returns>
    internal static string NameOf(DatabaseType type)
    {
        foreach (var builtIn in BuiltIn)
        {
            if (builtIn.Storage.Type == type)
            {
                return builtIn.Name;
            }
        }

        return "unknown";
    }

    private static SqlType Create(string name, DatabaseType type) => new(name, new DatabaseTypeInfo(type), isPseudo: false);
}

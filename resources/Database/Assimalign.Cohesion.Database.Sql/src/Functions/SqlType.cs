using System;
using System.Globalization;

using Assimalign.Cohesion.Database.Sql.Internal;
using Assimalign.Cohesion.Database.Sql.Language.Internal;
using Assimalign.Cohesion.Database.Types;

namespace Assimalign.Cohesion.Database.Sql;

/// <summary>
/// A SQL type: one of the engine's storage types, a domain over one
/// (<see cref="CreateDomain"/>), or a pseudo-type a function declares for a parameter or its result
/// that stands for several (<see cref="Any"/>, <see cref="AnyElement"/>).
/// </summary>
/// <remarks>
/// <para>
/// The descriptor is open: a value is a name over the physical type the row codec stores
/// (<see cref="Storage"/>), so a domain over a base type (phase E3) needs no change to the codec,
/// the key encoding or the on-disk format. The instances are the built-in types below,
/// parameterized <see cref="Decimal(int, int)"/>, <see cref="VarChar(int)"/> and <see cref="Char(int)"/>, and the domains an
/// application creates and registers on its engine (<see cref="SqlTypeCollection.Add"/>), which
/// resolve in DDL and CAST through the same type catalog as the built-in types.
/// </para>
/// <para>
/// <b>A domain</b> (PostgreSQL's <c>CREATE DOMAIN</c>, <c>src/backend/commands/typecmds.c</c>) is a
/// named base type with an optional check: a column of the domain stores the base type, and every
/// write that assigns the domain, and every CAST to it, evaluates the check, as a column CHECK is
/// evaluated: only FALSE refuses the value, so NULL and a NULL result pass. A domain is always over a
/// built-in type in this iteration, and its base type's length, precision and scale bound its
/// values. A function parameter or result of a domain type is its base type: overload resolution
/// and signature identity reduce a domain to its base, as PostgreSQL's <c>func_get_detail</c> does.
/// </para>
/// <para>
/// A function's signature identity and overload resolution compare storage types only, as
/// PostgreSQL ignores a type modifier in a function's signature: <c>VARCHAR(20)</c> and
/// <see cref="Text"/> are the same parameter type, and a call is not checked against a length,
/// precision or scale for the existing variable-length/numeric native signatures. CHAR parameters
/// and results enforce their declared padding and length; domains also apply their base bounds.
/// </para>
/// <para>
/// Pseudo-types never describe a stored value. <see cref="Any"/> accepts an argument of any type,
/// each independently, and <see cref="AnyNumeric"/> an argument of any numeric type.
/// <see cref="AnyElement"/> accepts any type too, but every <see cref="AnyElement"/> argument of one
/// call must have the same type, and a result declared <see cref="AnyElement"/> has that type
/// (PostgreSQL's <c>anyelement</c>).
/// </para>
/// </remarks>
public sealed class SqlType : IEquatable<SqlType>
{
    private SqlType(string name, DatabaseTypeInfo storage, bool isPseudo, SqlType? baseType = null, SqlScalarFunction? check = null)
    {
        Name = name;
        Storage = storage;
        IsPseudo = isPseudo;
        BaseType = baseType;
        Check = check;
    }

    internal string? DefaultLiteral { get; private set; }
    internal bool IsNotNull { get; private set; }
    internal bool IsSqlDefined { get; private set; }

    internal static SqlType CreateSqlDomain(string name, SqlType baseType, SqlScalarFunction? check, string? defaultLiteral, bool notNull)
    {
        var domain = CreateDomainCore(name, baseType, check, sqlDefined: true);
        domain.DefaultLiteral = defaultLiteral;
        domain.IsNotNull = notNull;
        domain.IsSqlDefined = true;
        return domain;
    }

    /// <summary>Gets the type's SQL name, as a diagnostic or a signature shows it.</summary>
    public string Name { get; }

    /// <summary>
    /// Gets the physical type the row codec stores, with its length, precision and scale when the
    /// type declares them; <see cref="DatabaseType.Null"/> for a pseudo-type. A domain's is its base
    /// type's.
    /// </summary>
    public DatabaseTypeInfo Storage { get; }

    /// <summary>Gets whether the type is a pseudo-type, which stands for several storage types.</summary>
    public bool IsPseudo { get; }

    /// <summary>Gets a domain's base type: the built-in type its values are stored as; null for any other type.</summary>
    public SqlType? BaseType { get; }

    /// <summary>
    /// Gets a domain's check: an <see cref="SqlFunctionVolatility.Immutable"/> scalar function of one
    /// argument that returns BOOLEAN, evaluated for every value the domain is assigned; null for a
    /// domain without one and for any other type.
    /// </summary>
    public SqlScalarFunction? Check { get; }

    /// <summary>Gets whether the type is a domain (<see cref="CreateDomain"/>).</summary>
    public bool IsDomain => BaseType is not null;

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

    /// <summary>Gets bare <c>CHAR</c>, equivalent to <c>CHAR(1)</c>.</summary>
    public static SqlType Character { get; } = Char(1);

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

    /// <summary>
    /// Gets <c>JSON</c>, JSON text. No CLR type of the typed shorthands maps to it, so a function over
    /// a JSON column is a leaf that declares a <see cref="Json"/> parameter and reads the argument with
    /// <see cref="SqlArguments.GetString(int)"/>; a <see cref="Text"/> parameter does not take a JSON value.
    /// </summary>
    public static SqlType Json { get; } = Create("JSON", DatabaseType.Json);

    /// <summary>
    /// Gets <c>JSONB</c>, binary JSON. As for <see cref="Json"/>, a function over it is a leaf that
    /// declares a <see cref="Jsonb"/> parameter and reads the argument with <see cref="SqlArguments.GetBinary(int)"/>.
    /// </summary>
    public static SqlType Jsonb { get; } = Create("JSONB", DatabaseType.JsonBinary);

    /// <summary>Gets the pseudo-type that accepts an argument of any type, each argument independently.</summary>
    public static SqlType Any { get; } = new("ANY", new DatabaseTypeInfo(DatabaseType.Null), isPseudo: true);

    /// <summary>
    /// Gets the polymorphic pseudo-type: every <see cref="AnyElement"/> argument of a call has one
    /// type, and a result declared <see cref="AnyElement"/> has that type.
    /// </summary>
    public static SqlType AnyElement { get; } = new("ANYELEMENT", new DatabaseTypeInfo(DatabaseType.Null), isPseudo: true);

    /// <summary>
    /// Gets the numeric pseudo-type: an argument of any numeric storage type (the integers,
    /// <see cref="Real"/>, <see cref="Double"/> and <see cref="Numeric"/>), each argument
    /// independently, as the standard library's <c>SUM</c> and <c>AVG</c> take it. A call over any
    /// other type is refused while planning. The value reaches the function as it is, so the function
    /// reads it with the accessor of its <see cref="SqlValue.Type"/>; a result cannot be declared
    /// <see cref="AnyNumeric"/>.
    /// </summary>
    public static SqlType AnyNumeric { get; } = new("ANYNUMERIC", new DatabaseTypeInfo(DatabaseType.Null), isPseudo: true);

    /// <summary>Gets the built-in storage types, in declaration order; the pseudo-types are not among them.</summary>
    internal static SqlType[] BuiltIn { get; } =
    [
        Boolean, TinyInt, SmallInt, Integer, BigInt, Real, Double, Numeric, Text, Character, Binary,
        Date, Time, Timestamp, TimestampTz, Interval, Uuid, Json, Jsonb,
    ];

    /// <summary>Creates <c>NUMERIC(precision, scale)</c>.</summary>
    /// <param name="precision">The total number of significant digits, from 1 through 28.</param>
    /// <param name="scale">The digits after the decimal point, 0 to <paramref name="precision"/>.</param>
    /// <returns>The type.</returns>
    /// <exception cref="ArgumentOutOfRangeException">The precision or the scale is outside its range.</exception>
    public static SqlType Decimal(int precision, int scale)
    {
        if (SqlTypeModifiers.GetError(DatabaseType.Decimal, null, precision, null) is { } precisionError)
        {
            throw new ArgumentOutOfRangeException(nameof(precision), precision, precisionError);
        }
        if (SqlTypeModifiers.GetError(DatabaseType.Decimal, null, precision, scale) is { } scaleError)
        {
            throw new ArgumentOutOfRangeException(nameof(scale), scale, scaleError);
        }
        return new(
            string.Create(CultureInfo.InvariantCulture, $"NUMERIC({precision}, {scale})"),
            new DatabaseTypeInfo(DatabaseType.Decimal, precision: precision, scale: scale),
            isPseudo: false);
    }

    /// <summary>Creates a fixed-length, blank-padded <c>CHAR(length)</c> type.</summary>
    /// <param name="length">The number of Unicode characters, from 1 through 10485760.</param>
    /// <returns>The fixed-length character type.</returns>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="length"/> is outside the supported range.</exception>
    public static SqlType Char(int length)
    {
        if (SqlTypeModifiers.GetError(DatabaseType.Character, length, null, null) is { } error)
        {
            throw new ArgumentOutOfRangeException(nameof(length), length, error);
        }
        return new(string.Create(CultureInfo.InvariantCulture, $"CHAR({length})"),
            new DatabaseTypeInfo(DatabaseType.Character, maxLength: length), isPseudo: false);
    }

    /// <summary>Creates <c>VARCHAR(maxLength)</c>.</summary>
    /// <param name="maxLength">The most characters a value holds, from 1 through 10485760.</param>
    /// <returns>The type.</returns>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="maxLength"/> is outside 1 through 10485760.</exception>
    public static SqlType VarChar(int maxLength)
    {
        if (SqlTypeModifiers.GetError(DatabaseType.String, maxLength, null, null) is { } error)
        {
            throw new ArgumentOutOfRangeException(nameof(maxLength), maxLength, error);
        }
        return new(
            string.Create(CultureInfo.InvariantCulture, $"VARCHAR({maxLength})"),
            new DatabaseTypeInfo(DatabaseType.String, maxLength: maxLength),
            isPseudo: false);
    }

    /// <summary>
    /// Creates a domain: a named type over a built-in base type, with an optional check (phase E3,
    /// owner decision 68 of 2026-10-09). Register it on the engine's builder
    /// (<see cref="SqlTypeCollection.Add"/>) before DDL or CAST can name it.
    /// </summary>
    /// <param name="name">
    /// The name DDL and CAST write, matched ignoring case: an ASCII identifier (an ASCII letter or
    /// underscore, then ASCII letters, digits and underscores, the rule the typed schema applies) that
    /// is neither a SQL keyword nor a built-in type name or alias (<c>INT</c>, <c>VARCHAR</c>,
    /// <c>DATE</c>, ...). SERIAL, BIGSERIAL and SMALLSERIAL are refused with CK00001 and an
    /// IDENTITY suggestion because the type grammar reserves those spellings.
    /// </param>
    /// <param name="baseType">
    /// The built-in type the domain's values are stored as, with its length, precision and scale
    /// (<see cref="VarChar(int)"/>, <see cref="Decimal(int, int)"/>): not a pseudo-type and, in this
    /// iteration, not another domain.
    /// </param>
    /// <param name="check">
    /// The domain's check, or null for none: a scalar function of one argument whose parameter takes
    /// the base type (its storage type, a type it widens to implicitly, or a pseudo-type), that
    /// returns BOOLEAN and is <see cref="SqlFunctionVolatility.Immutable"/>, the rule a CHECK
    /// constraint's functions follow (owner decision 64). A value it returns FALSE for is refused;
    /// TRUE and NULL pass, so a strict check passes NULL without being called.
    /// </param>
    /// <returns>The domain.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="name"/> or <paramref name="baseType"/> is null.</exception>
    /// <exception cref="ArgumentException">
    /// The name breaks a rule above; the base type is a pseudo-type or a domain; or the check does not
    /// take one argument of the base type, does not return BOOLEAN, or is not immutable.
    /// </exception>
    /// <example>
    /// <code>
    /// SqlType email = SqlType.CreateDomain("email", SqlType.VarChar(320),
    ///     SqlScalarFunction.Create("is_email", static (string value) =&gt; value.Contains('@'), SqlFunctionVolatility.Immutable));
    /// sql.Types.Add(email);
    /// </code>
    /// </example>
    public static SqlType CreateDomain(string name, SqlType baseType, SqlScalarFunction? check = null)
        => CreateDomainCore(name, baseType, check, sqlDefined: false);

    private static SqlType CreateDomainCore(string name, SqlType baseType, SqlScalarFunction? check, bool sqlDefined)
    {
        ArgumentNullException.ThrowIfNull(name);
        ArgumentNullException.ThrowIfNull(baseType);
        if (sqlDefined) { SqlTypeRules.ValidateSqlDomainName(name); }
        else { SqlTypeRules.ValidateDomainName(name); }
        if (baseType.IsPseudo)
        {
            throw new ArgumentException(
                string.Format(System.Globalization.CultureInfo.CurrentCulture, ErrorMessages.SqlTypeCreateDomainCoreArgument, name, baseType.Name), nameof(baseType));
        }
        if (baseType.IsDomain)
        {
            throw new ArgumentException(
                string.Format(System.Globalization.CultureInfo.CurrentCulture, ErrorMessages.SqlTypeCreateDomainCoreArgument2, name, baseType.Name, baseType.BaseType!.Name), nameof(baseType));
        }

        if (check is not null)
        {
            if (!check.AcceptsCount(1) || !SqlFunctionResolver.AcceptsType(check.ParameterAt(0), baseType.Storage.Type))
            {
                throw new ArgumentException(
                    string.Format(System.Globalization.CultureInfo.CurrentCulture, ErrorMessages.SqlTypeCreateDomainCoreArgument3, name, check, baseType.Name), nameof(check));
            }
            if (check.ReturnType.IsPseudo || check.ReturnType.Storage.Type != DatabaseType.Boolean)
            {
                throw new ArgumentException(
                    string.Format(System.Globalization.CultureInfo.CurrentCulture, ErrorMessages.SqlTypeCreateDomainCoreArgument4, name, check, check.ReturnType.Name), nameof(check));
            }
            if (check.Volatility != SqlFunctionVolatility.Immutable)
            {
                throw new ArgumentException(
                    string.Format(System.Globalization.CultureInfo.CurrentCulture, ErrorMessages.SqlTypeCreateDomainCoreArgument5, name, check, check.Volatility.ToString().ToUpperInvariant()), nameof(check));
            }
        }

        return new SqlType(name, baseType.Storage, isPseudo: false, baseType, check);
    }

    /// <summary>
    /// The built-in type of a storage type and its length, precision and scale, as the dialect's
    /// type names resolve one (<c>VARCHAR(10)</c> is <see cref="VarChar(int)"/> of 10, <c>INT</c>
    /// is <see cref="Integer"/>).
    /// </summary>
    /// <param name="storage">The storage type and its constraints.</param>
    /// <returns>The built-in type.</returns>
    internal static SqlType ForStorage(DatabaseTypeInfo storage)
    {
        if (storage.Type == DatabaseType.Character)
        {
            return storage.MaxLength is int length ? Char(length) : new SqlType("CHAR", storage, isPseudo: false);
        }
        int? scale = SqlTypeModifiers.NormalizeScale(storage.Type, storage.Precision, storage.Scale);
        if (scale != storage.Scale)
        {
            storage = new DatabaseTypeInfo(storage.Type, storage.MaxLength, storage.Precision, scale);
        }
        foreach (var builtIn in BuiltIn)
        {
            if (builtIn.Storage.Type == storage.Type)
            {
                if (storage.MaxLength is null && storage.Precision is null && storage.Scale is null)
                {
                    return builtIn;
                }

                string name = storage.Type == DatabaseType.Decimal && storage.Precision is { } precision
                    ? string.Create(CultureInfo.InvariantCulture, $"NUMERIC({precision}, {storage.Scale})")
                    : storage.MaxLength is { } length
                        ? string.Create(CultureInfo.InvariantCulture, $"{(storage.Type == DatabaseType.String ? "VARCHAR" : builtIn.Name)}({length})")
                        : builtIn.Name;
                return new SqlType(name, storage, isPseudo: false);
            }
        }

        throw new ArgumentException(string.Format(System.Globalization.CultureInfo.CurrentCulture, ErrorMessages.SqlTypeForStorageArgument, storage.Type), nameof(storage));
    }

    /// <inheritdoc />
    public bool Equals(SqlType? other)
        => other is not null && (ReferenceEquals(this, other)
            || IsPseudo == other.IsPseudo
            && IsDomain == other.IsDomain
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

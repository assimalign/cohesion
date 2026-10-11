using System;
using System.Globalization;

namespace Assimalign.Cohesion.Database.Sql.Internal;

/// <summary>
/// The SQL engine's built-in casts: entries of the cast registry, created with the public
/// <see cref="SqlCast.Create"/> and registered through the same <see cref="SqlTypeCollection.AddCast"/>
/// an application calls (phase E3), so <c>CAST</c> runs the engine's conversions exactly as it runs
/// an application's.
/// </summary>
/// <remarks>
/// <para>
/// The pairs are the ones the engine converted before E3 (text, BOOLEAN, the integers and NUMERIC
/// among themselves, and those to text), with no grouping and a value out of the target's range
/// refused. Declared NUMERIC scales round before checking precision. E3 adds REAL and DOUBLE from text and the
/// numeric types, and DATE, TIME, TIMESTAMP, TIMESTAMPTZ, INTERVAL and UUID from text (and DATE from
/// TIMESTAMP), parsed as INSERT parses text for a column of the type, so a string reaches a typed
/// function parameter through <c>CAST</c>. The contexts are PostgreSQL's for the same pairs
/// (<c>src/include/catalog/pg_cast.dat</c>): widenings implicit, narrowings and conversions to text
/// assignment, conversions from text explicit.
/// </para>
/// <para>
/// Each conversion maps storage type to storage type; the engine applies the target's length,
/// precision and scale afterwards (<see cref="SqlCastConverter.ApplyModifiers"/>). The built-in
/// text-to-NUMERIC entry is target-aware in <see cref="SqlCastConverter.Convert"/>: it rounds the
/// original digits to a declared scale before reducing them to Decimal, avoiding double rounding.
/// Application casts retain their delegates and receive the usual modifiers afterwards. Each delegate fails only
/// with a <see cref="FormatException"/>, an <see cref="OverflowException"/> or an
/// <see cref="InvalidCastException"/>, the value's faults <c>CAST</c> reports as conversion failures;
/// anything else a cast throws is a fault of its code (<c>COHSQLE007</c>), so a built-in that threw
/// another type would be reported as one. DATE, TIMESTAMP and TIMESTAMPTZ text reads through
/// <see cref="SqlTemporalText"/>, as INSERT reads it.
/// </para>
/// <para>
/// Not here, for now (Sql DESIGN, "Not in E3"): the date, time, UUID and binary floating-point types
/// to text, and REAL and DOUBLE to NUMERIC. An application registers any of them through the same
/// <see cref="SqlTypeCollection.AddCast"/>.
/// </para>
/// </remarks>
internal static class SqlStandardCasts
{
    private const SqlCastContext Explicit = SqlCastContext.Explicit;
    private const SqlCastContext Assignment = SqlCastContext.Assignment;
    private const SqlCastContext Implicit = SqlCastContext.Implicit;

    private static readonly SqlCast[] _casts = Create();

    /// <summary>Registers the built-in casts through the public registration path.</summary>
    /// <param name="types">The collection.</param>
    internal static void Register(SqlTypeCollection types)
    {
        foreach (var cast in _casts)
        {
            types.AddCast(cast);
        }
    }

    /// <summary>Whether a cast instance is one of the built-ins.</summary>
    /// <param name="cast">The cast.</param>
    /// <returns><see langword="true"/> for a standard cast.</returns>
    internal static bool Contains(SqlCast cast) => Array.IndexOf(_casts, cast) >= 0;

    private static SqlCast[] Create() =>
    [
        // To text: PostgreSQL's I/O conversions to a string type are assignment casts.
        SqlCast.Create<bool, string>(SqlType.Boolean, SqlType.Text, static value => value ? "TRUE" : "FALSE", Assignment),
        SqlCast.Create<sbyte, string>(SqlType.TinyInt, SqlType.Text, static value => value.ToString(CultureInfo.InvariantCulture), Assignment),
        SqlCast.Create<short, string>(SqlType.SmallInt, SqlType.Text, static value => value.ToString(CultureInfo.InvariantCulture), Assignment),
        SqlCast.Create<int, string>(SqlType.Integer, SqlType.Text, static value => value.ToString(CultureInfo.InvariantCulture), Assignment),
        SqlCast.Create<long, string>(SqlType.BigInt, SqlType.Text, static value => value.ToString(CultureInfo.InvariantCulture), Assignment),
        SqlCast.Create<decimal, string>(SqlType.Numeric, SqlType.Text, static value => value.ToString(CultureInfo.InvariantCulture), Assignment),

        // From text: explicit only.
        SqlCast.Create<string, bool>(SqlType.Text, SqlType.Boolean, static text => bool.TryParse(text.Trim(), out bool parsed)
            ? parsed
            : throw new InvalidCastException("BOOLEAN requires a boolean or TRUE/FALSE text."), Explicit),
        SqlCast.Create<string, sbyte>(SqlType.Text, SqlType.TinyInt, static text => checked((sbyte)Whole(SqlCastConverter.ParseExactDecimal(text))), Explicit),
        SqlCast.Create<string, short>(SqlType.Text, SqlType.SmallInt, static text => checked((short)Whole(SqlCastConverter.ParseExactDecimal(text))), Explicit),
        SqlCast.Create<string, int>(SqlType.Text, SqlType.Integer, static text => checked((int)Whole(SqlCastConverter.ParseExactDecimal(text))), Explicit),
        SqlCast.Create<string, long>(SqlType.Text, SqlType.BigInt, static text => checked((long)Whole(SqlCastConverter.ParseExactDecimal(text))), Explicit),
        SqlCast.Create<string, decimal>(SqlType.Text, SqlType.Numeric, static text => SqlCastConverter.ParseExactDecimal(text), Explicit),
        SqlCast.Create<string, float>(SqlType.Text, SqlType.Real, static text => ToReal(ParseFloat(text)), Explicit),
        SqlCast.Create<string, double>(SqlType.Text, SqlType.Double, static text => ParseFloat(text), Explicit),
        SqlCast.Create<string, DateOnly>(SqlType.Text, SqlType.Date, static text => SqlTemporalText.ParseDate(text), Explicit),
        SqlCast.Create<string, TimeOnly>(SqlType.Text, SqlType.Time, static text => TimeOnly.Parse(text, CultureInfo.InvariantCulture), Explicit),
        SqlCast.Create<string, DateTime>(SqlType.Text, SqlType.Timestamp, static text => SqlTemporalText.ParseTimestamp(text), Explicit),
        SqlCast.Create<string, DateTimeOffset>(SqlType.Text, SqlType.TimestampTz, static text => SqlTemporalText.ParseTimestampTz(text), Explicit),
        SqlCast.Create<string, TimeSpan>(SqlType.Text, SqlType.Interval, static text => TimeSpan.Parse(text, CultureInfo.InvariantCulture), Explicit),
        SqlCast.Create<string, Guid>(SqlType.Text, SqlType.Uuid, static text => Guid.Parse(text), Explicit),

        // Among the exact numbers: widenings implicit, narrowings assignment and range-checked.
        SqlCast.Create<sbyte, short>(SqlType.TinyInt, SqlType.SmallInt, static value => value, Implicit),
        SqlCast.Create<sbyte, int>(SqlType.TinyInt, SqlType.Integer, static value => value, Implicit),
        SqlCast.Create<sbyte, long>(SqlType.TinyInt, SqlType.BigInt, static value => value, Implicit),
        SqlCast.Create<sbyte, decimal>(SqlType.TinyInt, SqlType.Numeric, static value => value, Implicit),
        SqlCast.Create<short, sbyte>(SqlType.SmallInt, SqlType.TinyInt, static value => checked((sbyte)value), Assignment),
        SqlCast.Create<short, int>(SqlType.SmallInt, SqlType.Integer, static value => value, Implicit),
        SqlCast.Create<short, long>(SqlType.SmallInt, SqlType.BigInt, static value => value, Implicit),
        SqlCast.Create<short, decimal>(SqlType.SmallInt, SqlType.Numeric, static value => value, Implicit),
        SqlCast.Create<int, sbyte>(SqlType.Integer, SqlType.TinyInt, static value => checked((sbyte)value), Assignment),
        SqlCast.Create<int, short>(SqlType.Integer, SqlType.SmallInt, static value => checked((short)value), Assignment),
        SqlCast.Create<int, long>(SqlType.Integer, SqlType.BigInt, static value => value, Implicit),
        SqlCast.Create<int, decimal>(SqlType.Integer, SqlType.Numeric, static value => value, Implicit),
        SqlCast.Create<long, sbyte>(SqlType.BigInt, SqlType.TinyInt, static value => checked((sbyte)value), Assignment),
        SqlCast.Create<long, short>(SqlType.BigInt, SqlType.SmallInt, static value => checked((short)value), Assignment),
        SqlCast.Create<long, int>(SqlType.BigInt, SqlType.Integer, static value => checked((int)value), Assignment),
        SqlCast.Create<long, decimal>(SqlType.BigInt, SqlType.Numeric, static value => value, Implicit),
        SqlCast.Create<decimal, sbyte>(SqlType.Numeric, SqlType.TinyInt, static value => checked((sbyte)Whole(value)), Assignment),
        SqlCast.Create<decimal, short>(SqlType.Numeric, SqlType.SmallInt, static value => checked((short)Whole(value)), Assignment),
        SqlCast.Create<decimal, int>(SqlType.Numeric, SqlType.Integer, static value => checked((int)Whole(value)), Assignment),
        SqlCast.Create<decimal, long>(SqlType.Numeric, SqlType.BigInt, static value => checked((long)Whole(value)), Assignment),

        // To the binary floating-point types: from an exact number implicit; DOUBLE to REAL assignment.
        SqlCast.Create<sbyte, float>(SqlType.TinyInt, SqlType.Real, static value => value, Implicit),
        SqlCast.Create<short, float>(SqlType.SmallInt, SqlType.Real, static value => value, Implicit),
        SqlCast.Create<int, float>(SqlType.Integer, SqlType.Real, static value => value, Implicit),
        SqlCast.Create<long, float>(SqlType.BigInt, SqlType.Real, static value => value, Implicit),
        SqlCast.Create<decimal, float>(SqlType.Numeric, SqlType.Real, static value => (float)value, Implicit),
        SqlCast.Create<double, float>(SqlType.Double, SqlType.Real, static value => ToReal(value), Assignment),
        SqlCast.Create<sbyte, double>(SqlType.TinyInt, SqlType.Double, static value => value, Implicit),
        SqlCast.Create<short, double>(SqlType.SmallInt, SqlType.Double, static value => value, Implicit),
        SqlCast.Create<int, double>(SqlType.Integer, SqlType.Double, static value => value, Implicit),
        SqlCast.Create<long, double>(SqlType.BigInt, SqlType.Double, static value => value, Implicit),
        SqlCast.Create<decimal, double>(SqlType.Numeric, SqlType.Double, static value => (double)value, Implicit),
        SqlCast.Create<float, double>(SqlType.Real, SqlType.Double, static value => value, Implicit),

        // TIMESTAMP to DATE drops the time of day, as INSERT does for a DATE column.
        SqlCast.Create<DateTime, DateOnly>(SqlType.Timestamp, SqlType.Date, static value => DateOnly.FromDateTime(value), Assignment),
    ];

    /// <summary>Refuses a fractional value for an integer target: truncation is not supported.</summary>
    private static decimal Whole(decimal value)
        => decimal.Truncate(value) == value
            ? value
            : throw new InvalidCastException("An integer target requires a whole number; truncation is not supported.");

    /// <summary>Reads text as a binary floating-point number: no grouping, as for the exact numbers.</summary>
    private static double ParseFloat(string text)
        => double.Parse(text.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture);

    /// <summary>Narrows to REAL, refusing a finite value REAL overflows or underflows to zero, as PostgreSQL does.</summary>
    private static float ToReal(double value)
    {
        float narrowed = (float)value;
        if (float.IsInfinity(narrowed) && !double.IsInfinity(value))
        {
            throw new OverflowException($"Value {value.ToString(CultureInfo.InvariantCulture)} is out of range for REAL.");
        }
        if (narrowed == 0f && value != 0d)
        {
            throw new OverflowException($"Value {value.ToString(CultureInfo.InvariantCulture)} underflows REAL.");
        }

        return narrowed;
    }
}

using System;
using System.Globalization;

using Assimalign.Cohesion.Database.Sql.Language;

namespace Assimalign.Cohesion.Database.Sql.Internal;

/// <summary>
/// The SQL engine's built-in casts: entries of the cast registry, registered through the same
/// <see cref="SqlTypeCollection.AddCast"/>
/// an application calls (phase E3), so <c>CAST</c> runs the engine's conversions exactly as it runs
/// an application's.
/// </summary>
/// <remarks>
/// Entries convert storage values; CAST applies target modifiers and domain checks afterwards.
/// Exact numeric widenings are implicit, narrowings and output to character types assignment,
/// and text input explicit. JSON/JSONB pairs are assignment casts. Literal text uses SqlLiteralText's strict
/// grammar; assignment's SqlTemporalText reader retains its separate input acceptance.
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
        // PostgreSQL pg_cast.dat:318-325: bpchar -> text strips, text -> bpchar pads
        // only when the target modifier is applied (varchar shares the text identity here).
        SqlCast.Create<string, string>(SqlType.Character, SqlType.Text, static value => value.TrimEnd(' '), Implicit),
        SqlCast.Create<string, string>(SqlType.Text, SqlType.Character, static value => value, Implicit),

        // To text: PostgreSQL's I/O conversions to a string type are assignment casts.
        SqlCast.Create<bool, string>(SqlType.Boolean, SqlType.Text, static value => value ? "TRUE" : "FALSE", Assignment),
        SqlCast.Create<sbyte, string>(SqlType.TinyInt, SqlType.Text, static value => value.ToString(CultureInfo.InvariantCulture), Assignment),
        SqlCast.Create<short, string>(SqlType.SmallInt, SqlType.Text, static value => value.ToString(CultureInfo.InvariantCulture), Assignment),
        SqlCast.Create<int, string>(SqlType.Integer, SqlType.Text, static value => value.ToString(CultureInfo.InvariantCulture), Assignment),
        SqlCast.Create<long, string>(SqlType.BigInt, SqlType.Text, static value => value.ToString(CultureInfo.InvariantCulture), Assignment),
        SqlCast.Create<decimal, string>(SqlType.Numeric, SqlType.Text, static value => value.ToString(CultureInfo.InvariantCulture), Assignment),

        SqlCast.Create<float, string>(SqlType.Real, SqlType.Text, static value => SqlCastText.Format(value), Assignment),
        SqlCast.Create<float, string>(SqlType.Real, SqlType.Character, static value => SqlCastText.Format(value), Assignment),
        SqlCast.Create<double, string>(SqlType.Double, SqlType.Text, static value => SqlCastText.Format(value), Assignment),
        SqlCast.Create<double, string>(SqlType.Double, SqlType.Character, static value => SqlCastText.Format(value), Assignment),
        SqlCast.Create<DateOnly, string>(SqlType.Date, SqlType.Text, static value => SqlCastText.Format(value), Assignment),
        SqlCast.Create<DateOnly, string>(SqlType.Date, SqlType.Character, static value => SqlCastText.Format(value), Assignment),
        SqlCast.Create<TimeOnly, string>(SqlType.Time, SqlType.Text, static value => SqlCastText.Format(value), Assignment),
        SqlCast.Create<TimeOnly, string>(SqlType.Time, SqlType.Character, static value => SqlCastText.Format(value), Assignment),
        SqlCast.Create<DateTime, string>(SqlType.Timestamp, SqlType.Text, static value => SqlCastText.Format(value), Assignment),
        SqlCast.Create<DateTime, string>(SqlType.Timestamp, SqlType.Character, static value => SqlCastText.Format(value), Assignment),
        SqlCast.Create<DateTimeOffset, string>(SqlType.TimestampTz, SqlType.Text, static value => SqlCastText.Format(value), Assignment),
        SqlCast.Create<DateTimeOffset, string>(SqlType.TimestampTz, SqlType.Character, static value => SqlCastText.Format(value), Assignment),
        SqlCast.Create<TimeSpan, string>(SqlType.Interval, SqlType.Text, static value => SqlCastText.Format(value), Assignment),
        SqlCast.Create<TimeSpan, string>(SqlType.Interval, SqlType.Character, static value => SqlCastText.Format(value), Assignment),
        SqlCast.Create<Guid, string>(SqlType.Uuid, SqlType.Text, static value => SqlCastText.Format(value), Assignment),
        SqlCast.Create<Guid, string>(SqlType.Uuid, SqlType.Character, static value => SqlCastText.Format(value), Assignment),
        SqlCast.Create<byte[], string>(SqlType.Binary, SqlType.Text, static value => SqlCastText.Format(value), Assignment),
        SqlCast.Create<byte[], string>(SqlType.Binary, SqlType.Character, static value => SqlCastText.Format(value), Assignment),

        // From text: explicit only.
        SqlCast.Create<string, bool>(SqlType.Text, SqlType.Boolean, static text => bool.TryParse(text.Trim(), out bool parsed)
            ? parsed
            : throw new InvalidCastException("BOOLEAN requires a boolean or TRUE/FALSE text."), Explicit),
        SqlCast.Create<string, sbyte>(SqlType.Text, SqlType.TinyInt, static text => checked((sbyte)Whole(SqlCastConverter.ParseExactDecimal(text))), Explicit),
        SqlCast.Create<string, short>(SqlType.Text, SqlType.SmallInt, static text => checked((short)Whole(SqlCastConverter.ParseExactDecimal(text))), Explicit),
        SqlCast.Create<string, int>(SqlType.Text, SqlType.Integer, static text => checked((int)Whole(SqlCastConverter.ParseExactDecimal(text))), Explicit),
        SqlCast.Create<string, long>(SqlType.Text, SqlType.BigInt, static text => checked((long)Whole(SqlCastConverter.ParseExactDecimal(text))), Explicit),
        SqlCast.Create<string, decimal>(SqlType.Text, SqlType.Numeric, static text => SqlCastConverter.ParseExactDecimal(text), Explicit),
        SqlCast.Create<string, float>(SqlType.Text, SqlType.Real, static text => ParseReal(text), Explicit),
        SqlCast.Create<string, double>(SqlType.Text, SqlType.Double, static text => ParseFloat(text), Explicit),
        SqlCast.Create<string, DateOnly>(SqlType.Text, SqlType.Date, static text => SqlLiteralText.ParseDate(text.Trim()), Explicit),
        SqlCast.Create<string, TimeOnly>(SqlType.Text, SqlType.Time, static text => SqlLiteralText.ParseTime(text.Trim()), Explicit),
        SqlCast.Create<string, DateTime>(SqlType.Text, SqlType.Timestamp, static text => SqlLiteralText.ParseTimestamp(text.Trim()), Explicit),
        SqlCast.Create<string, DateTimeOffset>(SqlType.Text, SqlType.TimestampTz, static text => SqlLiteralText.ParseTimestampWithTimeZone(text.Trim()), Explicit),
        SqlCast.Create<string, TimeSpan>(SqlType.Text, SqlType.Interval, static text => SqlCastText.ParseInterval(text), Explicit),
        SqlCast.Create<string, Guid>(SqlType.Text, SqlType.Uuid, static text => Guid.Parse(text.Trim()), Explicit),

        SqlCast.Create<string, byte[]>(SqlType.Text, SqlType.Binary, static text => SqlCastText.ParseBinary(text), Explicit),
        new(SqlType.Text, SqlType.Json, Explicit, static value => SqlCastText.ValidateJson(SqlCastText.CharacterInput(value))),
        new(SqlType.Text, SqlType.Jsonb, Explicit, static value => SqlCastText.JsonBytes(SqlCastText.CharacterInput(value))),
        SqlCast.Create<string, byte[]>(SqlType.Character, SqlType.Binary, static text => SqlCastText.ParseBinary(text), Explicit),
        new(SqlType.Character, SqlType.Json, Explicit, static value => SqlCastText.ValidateJson(SqlCastText.CharacterInput(value))),
        new(SqlType.Character, SqlType.Jsonb, Explicit, static value => SqlCastText.JsonBytes(SqlCastText.CharacterInput(value))),
        new(SqlType.Json, SqlType.Text, Assignment, static value => (string)value),
        new(SqlType.Json, SqlType.Character, Assignment, static value => (string)value),
        new(SqlType.Jsonb, SqlType.Text, Assignment, static value => SqlCastText.JsonText((byte[])value)),
        new(SqlType.Jsonb, SqlType.Character, Assignment, static value => SqlCastText.JsonText((byte[])value)),
        new(SqlType.Json, SqlType.Jsonb, Assignment, static value => SqlCastText.JsonBytes((string)value)),
        new(SqlType.Jsonb, SqlType.Json, Assignment, static value => SqlCastText.JsonText((byte[])value)),

        // To CHAR: PostgreSQL's I/O conversions to a string type are assignment casts.
        SqlCast.Create<bool, string>(SqlType.Boolean, SqlType.Character, static value => value ? "TRUE" : "FALSE", Assignment),
        SqlCast.Create<sbyte, string>(SqlType.TinyInt, SqlType.Character, static value => value.ToString(CultureInfo.InvariantCulture), Assignment),
        SqlCast.Create<short, string>(SqlType.SmallInt, SqlType.Character, static value => value.ToString(CultureInfo.InvariantCulture), Assignment),
        SqlCast.Create<int, string>(SqlType.Integer, SqlType.Character, static value => value.ToString(CultureInfo.InvariantCulture), Assignment),
        SqlCast.Create<long, string>(SqlType.BigInt, SqlType.Character, static value => value.ToString(CultureInfo.InvariantCulture), Assignment),
        SqlCast.Create<decimal, string>(SqlType.Numeric, SqlType.Character, static value => value.ToString(CultureInfo.InvariantCulture), Assignment),

        // From CHAR: explicit only.
        SqlCast.Create<string, bool>(SqlType.Character, SqlType.Boolean, static text => bool.TryParse(text.Trim(), out bool parsed)
            ? parsed
            : throw new InvalidCastException("BOOLEAN requires a boolean or TRUE/FALSE text."), Explicit),
        SqlCast.Create<string, sbyte>(SqlType.Character, SqlType.TinyInt, static text => checked((sbyte)Whole(SqlCastConverter.ParseExactDecimal(text))), Explicit),
        SqlCast.Create<string, short>(SqlType.Character, SqlType.SmallInt, static text => checked((short)Whole(SqlCastConverter.ParseExactDecimal(text))), Explicit),
        SqlCast.Create<string, int>(SqlType.Character, SqlType.Integer, static text => checked((int)Whole(SqlCastConverter.ParseExactDecimal(text))), Explicit),
        SqlCast.Create<string, long>(SqlType.Character, SqlType.BigInt, static text => checked((long)Whole(SqlCastConverter.ParseExactDecimal(text))), Explicit),
        SqlCast.Create<string, decimal>(SqlType.Character, SqlType.Numeric, static text => SqlCastConverter.ParseExactDecimal(text), Explicit),
        SqlCast.Create<string, float>(SqlType.Character, SqlType.Real, static text => ParseReal(text), Explicit),
        SqlCast.Create<string, double>(SqlType.Character, SqlType.Double, static text => ParseFloat(text), Explicit),
        SqlCast.Create<string, DateOnly>(SqlType.Character, SqlType.Date, static text => SqlLiteralText.ParseDate(text.Trim()), Explicit),
        SqlCast.Create<string, TimeOnly>(SqlType.Character, SqlType.Time, static text => SqlLiteralText.ParseTime(text.Trim()), Explicit),
        SqlCast.Create<string, DateTime>(SqlType.Character, SqlType.Timestamp, static text => SqlLiteralText.ParseTimestamp(text.Trim()), Explicit),
        SqlCast.Create<string, DateTimeOffset>(SqlType.Character, SqlType.TimestampTz, static text => SqlLiteralText.ParseTimestampWithTimeZone(text.Trim()), Explicit),
        SqlCast.Create<string, TimeSpan>(SqlType.Character, SqlType.Interval, static text => SqlCastText.ParseInterval(text), Explicit),
        SqlCast.Create<string, Guid>(SqlType.Character, SqlType.Uuid, static text => Guid.Parse(text.Trim()), Explicit),

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
        // PostgreSQL numeric.c:4691-4696 goes directly from numeric_out to float4in.
        SqlCast.Create<decimal, float>(SqlType.Numeric, SqlType.Real,
            static value => float.Parse(value.ToString(CultureInfo.InvariantCulture), NumberStyles.Float, CultureInfo.InvariantCulture), Implicit),
        SqlCast.Create<double, float>(SqlType.Double, SqlType.Real, static value => ToReal(value), Assignment),
        SqlCast.Create<sbyte, double>(SqlType.TinyInt, SqlType.Double, static value => value, Implicit),
        SqlCast.Create<short, double>(SqlType.SmallInt, SqlType.Double, static value => value, Implicit),
        SqlCast.Create<int, double>(SqlType.Integer, SqlType.Double, static value => value, Implicit),
        SqlCast.Create<long, double>(SqlType.BigInt, SqlType.Double, static value => value, Implicit),
        SqlCast.Create<decimal, double>(SqlType.Numeric, SqlType.Double, static value => (double)value, Implicit),
        SqlCast.Create<float, double>(SqlType.Real, SqlType.Double, static value => value, Implicit),

        SqlCast.Create<float, sbyte>(SqlType.Real, SqlType.TinyInt, static value => checked((sbyte)WholeFloat(value)), Assignment),
        SqlCast.Create<float, short>(SqlType.Real, SqlType.SmallInt, static value => checked((short)WholeFloat(value)), Assignment),
        SqlCast.Create<float, int>(SqlType.Real, SqlType.Integer, static value => checked((int)WholeFloat(value)), Assignment),
        SqlCast.Create<float, long>(SqlType.Real, SqlType.BigInt, static value => checked((long)WholeFloat(value)), Assignment),
        SqlCast.Create<float, decimal>(SqlType.Real, SqlType.Numeric, static value => SqlCastConverter.ParseExactDecimal(value.ToString("R", CultureInfo.InvariantCulture), allowLiteralSyntax: true), Assignment),

        SqlCast.Create<double, sbyte>(SqlType.Double, SqlType.TinyInt, static value => checked((sbyte)WholeFloat(value)), Assignment),
        SqlCast.Create<double, short>(SqlType.Double, SqlType.SmallInt, static value => checked((short)WholeFloat(value)), Assignment),
        SqlCast.Create<double, int>(SqlType.Double, SqlType.Integer, static value => checked((int)WholeFloat(value)), Assignment),
        SqlCast.Create<double, long>(SqlType.Double, SqlType.BigInt, static value => checked((long)WholeFloat(value)), Assignment),
        SqlCast.Create<double, decimal>(SqlType.Double, SqlType.Numeric, static value => SqlCastConverter.ParseExactDecimal(value.ToString("R", CultureInfo.InvariantCulture), allowLiteralSyntax: true), Assignment),

        // Datetime contexts: PostgreSQL pg_cast.dat:400-423; JSON/JSONB:558-561.
        // Zone-dependent values follow DIALECT.md's timestamp normalization rule.
        SqlCast.Create<DateTime, DateOnly>(SqlType.Timestamp, SqlType.Date, static value => DateOnly.FromDateTime(value), Assignment),
        SqlCast.Create<DateOnly, DateTime>(SqlType.Date, SqlType.Timestamp, static value => value.ToDateTime(TimeOnly.MinValue), Implicit),
        SqlCast.Create<DateOnly, DateTimeOffset>(SqlType.Date, SqlType.TimestampTz, static value => new DateTimeOffset(value.ToDateTime(TimeOnly.MinValue), TimeSpan.Zero), Implicit),
        SqlCast.Create<DateTime, TimeOnly>(SqlType.Timestamp, SqlType.Time, static value => TimeOnly.FromDateTime(value), Assignment),
        SqlCast.Create<DateTime, DateTimeOffset>(SqlType.Timestamp, SqlType.TimestampTz, static value => new DateTimeOffset(DateTime.SpecifyKind(value, DateTimeKind.Unspecified), TimeSpan.Zero), Implicit),
        SqlCast.Create<DateTimeOffset, DateTime>(SqlType.TimestampTz, SqlType.Timestamp, static value => DateTime.SpecifyKind(value.UtcDateTime, DateTimeKind.Unspecified), Assignment),
        SqlCast.Create<DateTimeOffset, DateOnly>(SqlType.TimestampTz, SqlType.Date, static value => DateOnly.FromDateTime(value.UtcDateTime), Assignment),
        SqlCast.Create<DateTimeOffset, TimeOnly>(SqlType.TimestampTz, SqlType.Time, static value => TimeOnly.FromDateTime(value.UtcDateTime), Assignment),
        SqlCast.Create<TimeOnly, TimeSpan>(SqlType.Time, SqlType.Interval, static value => value.ToTimeSpan(), Implicit),
        SqlCast.Create<TimeSpan, TimeOnly>(SqlType.Interval, SqlType.Time, static value => value.Ticks >= 0 && value.Ticks < TimeSpan.TicksPerDay
            ? new TimeOnly(value.Ticks) : throw new InvalidCastException(ErrorMessages.CastIntervalTimeRange), Assignment),
    ];

    /// <summary>Refuses a fractional value for an integer target: truncation is not supported.</summary>
    private static decimal Whole(decimal value)
        => decimal.Truncate(value) == value
            ? value
            : throw new InvalidCastException("An integer target requires a whole number; truncation is not supported.");

    /// <summary>Reads text as a binary floating-point number: no grouping, as for the exact numbers.</summary>
    private static double ParseFloat(string text)
    {
        string input = text.Trim();
        double value = double.Parse(input, NumberStyles.Float, CultureInfo.InvariantCulture);
        if (double.IsInfinity(value) && !input.Equals("Infinity", StringComparison.OrdinalIgnoreCase)
            && !input.Equals("+Infinity", StringComparison.OrdinalIgnoreCase) && !input.Equals("-Infinity", StringComparison.OrdinalIgnoreCase))
        {
            throw new OverflowException(ErrorMessages.CastDoubleOverflow);
        }
        if (value == 0)
        {
            int exponent = input.IndexOfAny(['e', 'E']);
            ReadOnlySpan<char> mantissa = exponent < 0 ? input.AsSpan() : input.AsSpan(0, exponent);
            if (mantissa.ContainsAnyInRange('1', '9')) { throw new OverflowException(ErrorMessages.CastDoubleUnderflow); }
        }
        return value;
    }

    private static double WholeFloat(double value)
    {
        // PostgreSQL float.c:1267-1270 classifies NaN and infinities as range errors.
        if (!double.IsFinite(value)) { throw new OverflowException(ErrorMessages.CastIntegerRange); }
        return Math.Truncate(value) == value ? value : throw new InvalidCastException(ErrorMessages.CastIntegerWhole);
    }

    /// <summary>Narrows to REAL, refusing a finite value REAL overflows or underflows to zero, as PostgreSQL does.</summary>
    // A first rounding to DOUBLE can land on a REAL midpoint; parse directly for the target.
    private static float ParseReal(string text)
        => CheckReal(ParseFloat(text), float.Parse(text.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture));

    private static float ToReal(double value) => CheckReal(value, (float)value);

    private static float CheckReal(double value, float narrowed)
    {
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

using System;
using System.Globalization;
using Assimalign.Cohesion.Database.Graph.Language;

namespace Assimalign.Cohesion.Database.Graph.Internal;

internal static partial class GraphExpressionEvaluator
{
    private static object? Cast(object? value, GqlCastExpression cast)
    {
        if (value is null) { return null; }
        try
        {
            return cast.Target switch
            {
                GqlCastType.Int32 => checked((int)WholeInteger(value)),
                GqlCastType.Int64 => WholeInteger(value),
                GqlCastType.Float32 => Real(value),
                GqlCastType.Float64 => Floating(value),
                GqlCastType.Decimal => value is string text ? ExactDecimal(text) : value is float single ? ExactDecimal(single.ToString("R", CultureInfo.InvariantCulture), true) : value is double approximate ? ExactDecimal(approximate.ToString("R", CultureInfo.InvariantCulture), true) : IsNumber(value) ? Convert.ToDecimal(value, CultureInfo.InvariantCulture) : throw new InvalidCastException(ErrorMessages.NumericRequired),
                GqlCastType.String => ScalarText(value, cypher: false),
                GqlCastType.Boolean => value is bool boolean ? boolean : value is string text && bool.TryParse(text.Trim(), out bool parsed) ? parsed : throw new InvalidCastException(ErrorMessages.BooleanRequired),
                GqlCastType.Date => value is DateOnly date ? date : value is DateTimeOffset timestamp ? DateOnly.FromDateTime(timestamp.UtcDateTime) : value is string text ? ParseDate(text.Trim()) : throw new InvalidCastException(ErrorMessages.CastValueInvalid),
                GqlCastType.Timestamp => value is DateTimeOffset timestamp ? timestamp : value is DateOnly date ? new DateTimeOffset(date.ToDateTime(TimeOnly.MinValue), TimeSpan.Zero) : value is string text ? ParseTimestamp(text.Trim()) : throw new InvalidCastException(ErrorMessages.CastValueInvalid),
                _ => throw new InvalidCastException(ErrorMessages.CastValueInvalid),
            };
        }
        catch (Exception error) when (error is ArithmeticException or FormatException or InvalidCastException or ArgumentException)
        { throw new GraphPlanningException("COHDBG013", string.Format(CultureInfo.CurrentCulture, ErrorMessages.CastValueFailure, GraphScalarFunctions.Name(cast.Target)), cast.Location, error); }
    }

    // SQL #1119 rejects fractional integers rather than rounding or truncating.
    private static long WholeInteger(object value)
    {
        if (value is float or double)
        {
            double number = Convert.ToDouble(value, CultureInfo.InvariantCulture);
            if (!double.IsFinite(number) || number != Math.Truncate(number)) { throw new InvalidCastException(ErrorMessages.NumericRequired); }
            return checked((long)number);
        }
        decimal exact = value is string text ? ExactDecimal(text) : IsNumber(value) ? Convert.ToDecimal(value, CultureInfo.InvariantCulture) : throw new InvalidCastException(ErrorMessages.NumericRequired);
        if (exact != decimal.Truncate(exact)) { throw new InvalidCastException(ErrorMessages.NumericRequired); }
        return checked((long)exact);
    }

    // SQL SqlStandardCasts.cs:206-249 checks the mantissa for text underflow and
    // the original numeric value when narrowing to REAL.
    private static double Floating(object value)
    {
        double number = value is string text ? double.Parse(text, NumberStyles.Float, CultureInfo.InvariantCulture)
            : IsNumber(value) ? Convert.ToDouble(value, CultureInfo.InvariantCulture) : throw new InvalidCastException(ErrorMessages.NumericRequired);
        if (number == 0 && value is string source)
        {
            int exponent = source.IndexOfAny(['e', 'E']);
            ReadOnlySpan<char> mantissa = exponent < 0 ? source.AsSpan() : source.AsSpan(0, exponent);
            if (mantissa.ContainsAnyInRange('1', '9')) { throw new OverflowException(ErrorMessages.NumericRequired); }
        }
        return Finite(number);
    }

    private static float Real(object value)
    {
        double original = Floating(value);
        // SQL SqlStandardCasts.cs:161 parses decimal text directly, avoiding double rounding
        // (PostgreSQL src/backend/utils/adt/numeric.c:4691-4696).
        float number = value is string text ? float.Parse(text, NumberStyles.Float, CultureInfo.InvariantCulture)
            : value is decimal exact ? float.Parse(exact.ToString(CultureInfo.InvariantCulture), NumberStyles.Float, CultureInfo.InvariantCulture)
            : IsNumber(value) ? Convert.ToSingle(value, CultureInfo.InvariantCulture) : throw new InvalidCastException(ErrorMessages.NumericRequired);
        if (!float.IsFinite(number) || number == 0 && original != 0) { throw new OverflowException(ErrorMessages.NumericRequired); }
        return number;
    }

    // Adapted locally from SqlCastConverter.cs:256-330: preserve the exact coefficient/scale,
    // including round-trip floating text, without widening the Graph -> SQL assembly boundary.
    private static decimal ExactDecimal(string text, bool allowExponent = false)
    {
        string source = text.Trim();
        bool negative = source.StartsWith('-');
        if (source.StartsWith('+') || negative) { source = source[1..]; }
        int exponent = 0;
        int exponentStart = source.IndexOfAny(['e', 'E']);
        if (allowExponent && exponentStart >= 0)
        {
            exponent = int.Parse(source.AsSpan(exponentStart + 1), NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture);
            source = source[..exponentStart];
        }
        int point = source.IndexOf('.');
        if (source.Length == 0 || source == "." || !allowExponent && (point == 0 || point == source.Length - 1))
        { throw new FormatException(ErrorMessages.NumericRequired); }
        for (int i = 0; i < source.Length; i++)
        { if (i != point && !char.IsAsciiDigit(source[i])) { throw new FormatException(ErrorMessages.NumericRequired); } }
        long scale = (point < 0 ? 0L : source.Length - point - 1L) - exponent;
        string digits = (point < 0 ? source : source.Remove(point, 1)).TrimStart('0');
        if (digits.Length == 0) { return 0m; }
        string significant = digits.TrimEnd('0');
        scale -= digits.Length - significant.Length;
        if (scale > 28 || significant.Length > 29 || significant.Length - scale > 29)
        { throw new OverflowException(ErrorMessages.NumericRequired); }
        decimal coefficient = decimal.Parse(significant, NumberStyles.None, CultureInfo.InvariantCulture);
        while (scale < 0) { coefficient = checked(coefficient * 10m); scale++; }
        Span<int> bits = stackalloc int[4];
        decimal.GetBits(coefficient, bits);
        return new decimal(bits[0], bits[1], bits[2], negative, (byte)scale);
    }

    private static DateOnly ParseDate(string text)
        => text.Length == 10 && DateOnly.TryParseExact(text, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var date)
            ? date : throw new FormatException(ErrorMessages.CastValueInvalid);

    private static bool ValidTimeText(ReadOnlySpan<char> time)
    {
        if (time.Length is not (5 or 8 or >= 10 and <= 16) || time[2] != ':' || time.Length >= 8 && time[5] != ':' || time.Length > 8 && time[8] != '.') { return false; }
        for (int i = 0; i < time.Length; i++)
        { if (i is not (2 or 5 or 8) && !char.IsAsciiDigit(time[i])) { return false; } }
        return true;
    }

    // Same invariant grammar as SqlLiteralText: optional time and zone, absent zone means UTC.
    private static DateTimeOffset ParseTimestamp(string text)
    {
        int end = text.Length;
        TimeSpan offset = TimeSpan.Zero;
        if (end > 10 && text[^1] == 'Z') { end--; }
        else if (end >= 16 && text[^6] is '+' or '-')
        {
            var zone = text.AsSpan(end - 6);
            if (zone[3] != ':' || !int.TryParse(zone.Slice(1, 2), NumberStyles.None, CultureInfo.InvariantCulture, out int hours) ||
                !int.TryParse(zone.Slice(4, 2), NumberStyles.None, CultureInfo.InvariantCulture, out int minutes) || hours > 14 || minutes > 59 || hours == 14 && minutes != 0)
            { throw new FormatException(ErrorMessages.CastValueInvalid); }
            offset = TimeSpan.FromMinutes((hours * 60 + minutes) * (zone[0] == '-' ? -1 : 1));
            end -= 6;
        }
        if (end < 10) { throw new FormatException(ErrorMessages.CastValueInvalid); }
        var date = ParseDate(text[..10]);
        TimeOnly time = TimeOnly.MinValue;
        if (end > 10)
        {
            if (text[10] is not (' ' or 'T') || !ValidTimeText(text.AsSpan(11, end - 11)) || !TimeOnly.TryParseExact(text[11..end], ["HH:mm", "HH:mm:ss", "HH:mm:ss.FFFFFFF"], CultureInfo.InvariantCulture, DateTimeStyles.None, out time))
            { throw new FormatException(ErrorMessages.CastValueInvalid); }
        }
        return new DateTimeOffset(date.ToDateTime(time), offset);
    }
}

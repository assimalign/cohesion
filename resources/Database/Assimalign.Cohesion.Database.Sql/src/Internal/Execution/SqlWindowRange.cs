using System;
using System.Globalization;
using System.Numerics;

using Assimalign.Cohesion.Database.Types;

namespace Assimalign.Cohesion.Database.Sql.Internal;

/// <summary>Offset-domain checks and PostgreSQL-style in_range comparisons without boundary overflow.</summary>
internal static class SqlWindowRange
{
    internal static bool Supports(DatabaseType type) => SqlFunctionResolver.IsNumeric(type) ||
        type is DatabaseType.Date or DatabaseType.Time or DatabaseType.DateTime or DatabaseType.DateTimeOffset or DatabaseType.TimeSpan;

    internal static bool AcceptsOffset(DatabaseType key, DatabaseType offset) => offset == DatabaseType.Null || key switch
    {
        DatabaseType.Int8 or DatabaseType.Int16 or DatabaseType.Int32 or DatabaseType.Int64 =>
            offset is DatabaseType.Int8 or DatabaseType.Int16 or DatabaseType.Int32 or DatabaseType.Int64,
        DatabaseType.Decimal => offset is DatabaseType.Decimal or DatabaseType.Int8 or DatabaseType.Int16 or DatabaseType.Int32 or DatabaseType.Int64,
        DatabaseType.Float32 or DatabaseType.Float64 => SqlFunctionResolver.IsNumeric(offset),
        _ => offset == DatabaseType.TimeSpan,
    };

    internal static void ValidateOffset(object offset)
    {
        bool invalid = offset is TimeSpan duration ? duration.Ticks < 0 : offset is float or double
            ? double.IsNaN(Convert.ToDouble(offset, CultureInfo.InvariantCulture)) || Convert.ToDouble(offset, CultureInfo.InvariantCulture) < 0
            : Convert.ToDecimal(offset, CultureInfo.InvariantCulture) < 0;
        if (invalid) { throw new SqlExecutionException("CS02076", ErrorMessages.WindowRangeOffsetInvalid); }
    }

    internal static bool InRange(object value, object basis, object offset, bool subtract, bool less)
    {
        if (basis is float or double)
        {
            double number = Convert.ToDouble(value, CultureInfo.InvariantCulture);
            double origin = Convert.ToDouble(basis, CultureInfo.InvariantCulture);
            double distance = Convert.ToDouble(offset, CultureInfo.InvariantCulture);
            // SQL's established order puts NaN below -infinity. Keep that peer order here.
            if (double.IsNaN(number)) { return double.IsNaN(origin) || less; }
            if (double.IsNaN(origin)) { return !less; }
            // float.c:1113-1125: indeterminate infinity +/- infinity includes every key.
            if (double.IsPositiveInfinity(distance) && double.IsInfinity(origin) && (subtract ? origin > 0 : origin < 0)) { return true; }
            double boundary = subtract ? origin - distance : origin + distance;
            return less ? number <= boundary : number >= boundary;
        }
        if (offset is TimeSpan interval)
        {
            decimal boundary = Ticks(basis) + (subtract ? -(decimal)interval.Ticks : interval.Ticks);
            if (basis is DateOnly or DateTime or DateTimeOffset ? boundary < 0 || boundary > DateTime.MaxValue.Ticks
                : basis is TimeSpan && (boundary < long.MinValue || boundary > long.MaxValue))
            { throw new SqlExecutionException(basis is TimeSpan ? SqlExecutionException.IntervalOverflowCode : SqlExecutionException.DatetimeOverflowCode, ErrorMessages.WindowRangeBoundary); }
            return less ? Ticks(value) <= boundary : Ticks(value) >= boundary;
        }
        var target = Scaled(Convert.ToDecimal(basis, CultureInfo.InvariantCulture)) +
            (subtract ? -Scaled(Convert.ToDecimal(offset, CultureInfo.InvariantCulture)) : Scaled(Convert.ToDecimal(offset, CultureInfo.InvariantCulture)));
        int comparison = Scaled(Convert.ToDecimal(value, CultureInfo.InvariantCulture)).CompareTo(target);
        return less ? comparison <= 0 : comparison >= 0;
    }

    // The engine's INTERVAL is a TimeSpan duration. Time frames never wrap at midnight;
    // timestamps use wall-clock/UTC identity, consistently with SqlValueComparer.
    private static long Ticks(object value) => value switch
    {
        DateOnly date => date.ToDateTime(TimeOnly.MinValue).Ticks,
        TimeOnly time => time.Ticks,
        DateTime timestamp => timestamp.Ticks,
        DateTimeOffset timestamp => timestamp.UtcTicks,
        TimeSpan interval => interval.Ticks,
        _ => throw new SqlExecutionException("CS02074", ErrorMessages.WindowRangeKeyType),
    };

    internal static BigInteger Scaled(decimal value)
    {
        Span<int> bits = stackalloc int[4];
        decimal.GetBits(value, bits);
        var result = ((BigInteger)(uint)bits[2] << 64) | ((BigInteger)(uint)bits[1] << 32) | (uint)bits[0];
        result *= BigInteger.Pow(10, 28 - ((bits[3] >> 16) & 0xff));
        return bits[3] < 0 ? -result : result;
    }
}

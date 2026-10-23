using System;

// Deviates from the repo namespace-matches-assembly rule: each linked reader translates its own private failures before returning across an assembly boundary.
namespace Assimalign.Cohesion.Database.Sql.Language.Internal;

internal sealed class SqlTemporalReadException : Exception
{
    internal SqlTemporalReadException(SqlTemporalFailureKind kind, string message, Exception? innerException = null)
        : base(message, innerException)
    {
        Kind = kind;
    }

    internal SqlTemporalFailureKind Kind { get; }
}

internal enum SqlTemporalFailureKind { Format, FieldRange, IntervalOverflow, UnsupportedYearMonth }

internal enum SqlTemporalTextError
{
    DateTextInvalid,
    TimestampTextInvalid,
    TimestampSeparatorInvalid,
    TimestampZoneRequired,
    TimestampOffsetInvalid,
    TimestampOffsetRange,
    TimestampRangeInvalid,
    IntervalYearMonthUnsupported,
    TimeTextInvalid,
    IntervalTextInvalid
}

using System;

using Assimalign.Cohesion.Database.Language;
using Assimalign.Cohesion.Database.Sql.Language;
using Assimalign.Cohesion.Database.Types;

namespace Assimalign.Cohesion.Database.Sql.Internal;

internal static class SqlTemporalArithmetic
{
    internal static DatabaseType CommonCalendarType(DatabaseType left, DatabaseType right) =>
        left == right ? left : left == DatabaseType.DateTimeOffset || right == DatabaseType.DateTimeOffset ? DatabaseType.DateTimeOffset : DatabaseType.DateTime;

    internal static object? Promote(object? value, DatabaseType target) => value switch
    {
        DateOnly date when target == DatabaseType.DateTime => date.ToDateTime(TimeOnly.MinValue),
        DateOnly date when target == DatabaseType.DateTimeOffset => new DateTimeOffset(date.ToDateTime(TimeOnly.MinValue), TimeSpan.Zero),
        DateTime timestamp when target == DatabaseType.DateTimeOffset => new DateTimeOffset(DateTime.SpecifyKind(timestamp, DateTimeKind.Unspecified), TimeSpan.Zero),
        _ => value,
    };

    internal static bool IsCalendar(DatabaseType type) => type is DatabaseType.Date or DatabaseType.DateTime or DatabaseType.DateTimeOffset;
    internal static bool IsTemporal(DatabaseType type) => IsCalendar(type) || type is DatabaseType.Time or DatabaseType.TimeSpan;
    private static bool IsInteger(DatabaseType type) => type is DatabaseType.Int8 or DatabaseType.Int16 or DatabaseType.Int32 or DatabaseType.Int64;

    internal static DatabaseType ResultType(SqlBinaryOperator op, DatabaseType left, DatabaseType right)
    {
        if (left == DatabaseType.Null || right == DatabaseType.Null) { return DatabaseType.Null; }
        if (op is not (SqlBinaryOperator.Add or SqlBinaryOperator.Subtract)) { return DatabaseType.Null; }
        if (left == DatabaseType.Date && IsInteger(right)) { return DatabaseType.Date; }
        if (left == DatabaseType.Date && right == DatabaseType.Date && op == SqlBinaryOperator.Subtract) { return DatabaseType.Int32; }
        if (left == DatabaseType.TimeSpan && right == DatabaseType.TimeSpan) { return DatabaseType.TimeSpan; }
        if (right == DatabaseType.TimeSpan && IsCalendar(left)) { return left == DatabaseType.Date ? DatabaseType.DateTime : left; }
        if (left is DatabaseType.DateTime or DatabaseType.DateTimeOffset && right is DatabaseType.DateTime or DatabaseType.DateTimeOffset && op == SqlBinaryOperator.Subtract)
        { return DatabaseType.TimeSpan; }
        return DatabaseType.Null;
    }

    private static DateTimeOffset AddInstant(DateTimeOffset instant, TimeSpan interval)
    {
        var utc = new DateTimeOffset(instant.UtcDateTime.Add(interval), TimeSpan.Zero);
        long localTicks = utc.Ticks + instant.Offset.Ticks;
        return localTicks >= DateTime.MinValue.Ticks && localTicks <= DateTime.MaxValue.Ticks
            ? utc.ToOffset(instant.Offset) : utc;
    }

    internal static SqlPlanningException InvalidOperands(SqlBinaryOperator op, DatabaseType left, DatabaseType right, Location? location = null)
        => new("COHSQLE003", $"Invalid temporal arithmetic operands: {SqlType.NameOf(left)} {op switch { SqlBinaryOperator.Add => "+", SqlBinaryOperator.Subtract => "-", SqlBinaryOperator.Multiply => "*", SqlBinaryOperator.Divide => "/", SqlBinaryOperator.Modulo => "%", _ => op.ToString() }} {SqlType.NameOf(right)}.", location);

    internal static object Evaluate(SqlBinaryOperator op, object left, object right)
    {
        var leftType = SqlValue.FromObject(left).Type;
        var rightType = SqlValue.FromObject(right).Type;
        if (ResultType(op, leftType, rightType) == DatabaseType.Null) { throw InvalidOperands(op, leftType, rightType); }
        try
        {
            if (left is DateOnly date && right is sbyte or short or int or long)
            {
                long days = Convert.ToInt64(right, System.Globalization.CultureInfo.InvariantCulture);
                return date.AddDays(checked((int)(op == SqlBinaryOperator.Add ? days : checked(-days))));
            }
            if (left is DateOnly first && right is DateOnly second && op == SqlBinaryOperator.Subtract) { return first.DayNumber - second.DayNumber; }
            if (right is TimeSpan interval)
            {
                bool add = op == SqlBinaryOperator.Add;
                return left switch
                {
                    DateOnly day => add ? day.ToDateTime(TimeOnly.MinValue).Add(interval) : day.ToDateTime(TimeOnly.MinValue).Subtract(interval),
                    DateTime wall => add ? wall.Add(interval) : wall.Subtract(interval),
                    DateTimeOffset instant => AddInstant(instant, add ? interval : -interval),
                    TimeSpan duration => add ? duration.Add(interval) : duration.Subtract(interval),
                    _ => throw new SqlPlanningException("COHSQLE003", "Invalid temporal arithmetic operands."),
                };
            }
            if (left is DateTime or DateTimeOffset && right is DateTime or DateTimeOffset && op == SqlBinaryOperator.Subtract)
            {
                long leftTicks = left is DateTime wall ? wall.Ticks : ((DateTimeOffset)left).UtcTicks;
                long rightTicks = right is DateTime clock ? clock.Ticks : ((DateTimeOffset)right).UtcTicks;
                return new TimeSpan(leftTicks - rightTicks);
            }
            throw new SqlPlanningException("COHSQLE003", "Invalid temporal arithmetic operands.");
        }
        catch (ArgumentOutOfRangeException exception) { throw SqlEvaluationException.NumericValueOutOfRange("Temporal arithmetic is outside years 1..9999.", exception); }
        catch (ArithmeticException exception) { throw SqlEvaluationException.FromArithmetic(exception); }
    }
}

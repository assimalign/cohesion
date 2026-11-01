using System;
using System.Globalization;

using Assimalign.Cohesion.Database.Language;
using Assimalign.Cohesion.Database.Sql.Language;
using Assimalign.Cohesion.Database.Types;

namespace Assimalign.Cohesion.Database.Sql.Internal;

internal static class SqlBitwise
{
    internal static bool IsBinary(SqlBinaryOperator op) => op is SqlBinaryOperator.BitwiseAnd
        or SqlBinaryOperator.BitwiseOr or SqlBinaryOperator.BitwiseXor
        or SqlBinaryOperator.ShiftLeft or SqlBinaryOperator.ShiftRight;

    internal static bool IsShift(SqlBinaryOperator op) => op is SqlBinaryOperator.ShiftLeft or SqlBinaryOperator.ShiftRight;

    internal static void Validate(DatabaseType type, string op, Location? location, bool shiftCount = false)
    {
        if (type is not (DatabaseType.Int16 or DatabaseType.Int32 or DatabaseType.Int64 or DatabaseType.Null)
            || shiftCount && type == DatabaseType.Int64)
        {
            throw new SqlPlanningException("CS02077", string.Format(CultureInfo.CurrentCulture,
                shiftCount ? ErrorMessages.BitwiseShiftCountType : ErrorMessages.BitwiseOperandType, op, SqlType.NameOf(type)), location);
        }
    }

    internal static string Text(SqlBinaryOperator op) => op switch
    {
        SqlBinaryOperator.BitwiseAnd => "&",
        SqlBinaryOperator.BitwiseOr => "|",
        SqlBinaryOperator.BitwiseXor => "#",
        SqlBinaryOperator.ShiftLeft => "<<",
        _ => ">>",
    };

    internal static DatabaseType ResultType(SqlBinaryOperator op, DatabaseType left, DatabaseType right)
        => IsShift(op) ? left == DatabaseType.Null ? DatabaseType.Int32 : left
            : left == DatabaseType.Int64 || right == DatabaseType.Int64 ? DatabaseType.Int64
            : left == DatabaseType.Int32 || right == DatabaseType.Int32 ? DatabaseType.Int32
            : left == DatabaseType.Int16 || right == DatabaseType.Int16 ? DatabaseType.Int16 : DatabaseType.Int32;

    internal static object Binary(SqlBinaryOperator op, object left, object right, DatabaseType type, Location? location)
    {
        ValueType(left, Text(op), location);
        ValueType(right, Text(op), location);
        long a = Integer(left, type), b = Integer(right, IsShift(op) ? DatabaseType.Int32 : type);
        // PostgreSQL int.c:1449,1511,1520 and int8.c:1255: int2 shifts promote
        // to int4, then truncate; int4/int8 use their native arithmetic shifts.
        int count = unchecked((int)b);
        long result = op switch
        {
            SqlBinaryOperator.BitwiseAnd => a & b,
            SqlBinaryOperator.BitwiseOr => a | b,
            SqlBinaryOperator.BitwiseXor => a ^ b,
            SqlBinaryOperator.ShiftLeft => type == DatabaseType.Int64 ? unchecked(a << count) : unchecked((int)a << count),
            _ => type == DatabaseType.Int64 ? a >> count : (int)a >> count,
        };
        return Narrow(result, type);
    }

    internal static object Not(object value, DatabaseType type, Location? location)
    {
        ValueType(value, "~", location);
        return Narrow(~Integer(value, type), type);
    }

    private static long Integer(object value, DatabaseType type)
    {
        long integer = value switch { short n => n, int n => n, _ => (long)value };
        return type == DatabaseType.Int16 ? checked((short)integer) : type == DatabaseType.Int32 ? checked((int)integer) : integer;
    }

    internal static DatabaseType ValueType(object value, string op, Location? location) => value switch
    {
        short => DatabaseType.Int16,
        int => DatabaseType.Int32,
        long => DatabaseType.Int64,
        _ => throw new SqlPlanningException("CS02077", string.Format(CultureInfo.CurrentCulture,
            ErrorMessages.BitwiseOperandType, op, SqlExpressionEvaluator.OperandTypeName(value)), location),
    };

    // Separate returns preserve the boxed SQL width; a numeric switch would widen all arms.
    private static object Narrow(long value, DatabaseType type)
    {
        if (type == DatabaseType.Int16) { return unchecked((short)value); }
        if (type == DatabaseType.Int32) { return unchecked((int)value); }
        return value;
    }
}

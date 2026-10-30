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

    internal static void Validate(DatabaseType type, string op, Location? location)
    {
        if (type is not (DatabaseType.Int16 or DatabaseType.Int32 or DatabaseType.Int64 or DatabaseType.Null))
        {
            throw new SqlPlanningException("CS02077", string.Format(CultureInfo.CurrentCulture,
                ErrorMessages.BitwiseOperandType, op, SqlType.NameOf(type)), location);
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

    internal static DatabaseType ResultType(DatabaseType left, DatabaseType right)
        => left == DatabaseType.Int64 || right == DatabaseType.Int64 ? DatabaseType.Int64
            : left == DatabaseType.Int32 || right == DatabaseType.Int32 ? DatabaseType.Int32
            : left == DatabaseType.Int16 || right == DatabaseType.Int16 ? DatabaseType.Int16 : DatabaseType.Int32;

    internal static object Binary(SqlBinaryOperator op, object left, object right, Location? location)
    {
        var leftType = ValueType(left, Text(op), location);
        var rightType = ValueType(right, Text(op), location);
        var type = ResultType(leftType, rightType);
        long a = Integer(left), b = Integer(right);
        int width = type == DatabaseType.Int16 ? 16 : type == DatabaseType.Int32 ? 32 : 64;
        int count = unchecked((int)b) & (width - 1);
        long result = op switch
        {
            SqlBinaryOperator.BitwiseAnd => a & b,
            SqlBinaryOperator.BitwiseOr => a | b,
            SqlBinaryOperator.BitwiseXor => a ^ b,
            SqlBinaryOperator.ShiftLeft => unchecked(a << count),
            _ => a >> count,
        };
        return Narrow(result, type);
    }

    internal static object Not(object value, Location? location)
    {
        var type = ValueType(value, "~", location);
        return Narrow(~Integer(value), type);
    }

    private static long Integer(object value) => value switch { short n => n, int n => n, _ => (long)value };

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

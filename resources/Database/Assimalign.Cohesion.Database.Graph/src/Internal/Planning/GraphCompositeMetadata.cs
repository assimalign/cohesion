using System;
using Assimalign.Cohesion.Database.Types;

namespace Assimalign.Cohesion.Database.Graph.Internal;

internal static class GraphCompositeMetadata
{
    internal static DatabaseType Type(object? value) => value switch
    {
        null => DatabaseType.Null, bool => DatabaseType.Boolean, sbyte => DatabaseType.Int8,
        byte or short => DatabaseType.Int16, ushort or int => DatabaseType.Int32, uint or long => DatabaseType.Int64,
        float => DatabaseType.Float32, double => DatabaseType.Float64, decimal or ulong => DatabaseType.Decimal,
        string => DatabaseType.String, DateOnly => DatabaseType.Date, DateTimeOffset => DatabaseType.DateTimeOffset,
        _ => DatabaseType.Null,
    };

    internal static DatabaseType Widen(DatabaseType left, DatabaseType right)
    {
        if (left == DatabaseType.Null) { return right; }
        if (right == DatabaseType.Null || left == right) { return left; }
        bool leftNumber = left is >= DatabaseType.Int8 and <= DatabaseType.Decimal;
        bool rightNumber = right is >= DatabaseType.Int8 and <= DatabaseType.Decimal;
        if (!leftNumber || !rightNumber) { return DatabaseType.Null; }
        bool leftFloat = left is DatabaseType.Float32 or DatabaseType.Float64;
        bool rightFloat = right is DatabaseType.Float32 or DatabaseType.Float64;
        if (left == DatabaseType.Decimal || right == DatabaseType.Decimal)
        { return leftFloat || rightFloat ? DatabaseType.Null : DatabaseType.Decimal; }
        if (leftFloat || rightFloat)
        { return left == DatabaseType.Float32 && right == DatabaseType.Float32 ? DatabaseType.Float32 : DatabaseType.Float64; }
        return (DatabaseType)Math.Max((byte)left, (byte)right);
    }
}

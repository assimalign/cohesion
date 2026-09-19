using System;

using Assimalign.Cohesion.Database.Types;

namespace Assimalign.Cohesion.Database.Sql.Mapping;

internal static class SqlMappingText
{
    internal static string Identifier(string name)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        if (name.IndexOfAny(['\0', '"']) >= 0)
        {
            throw new ArgumentException("The executable SQL dialect does not support null characters or embedded double quotes in identifiers.", nameof(name));
        }
        return "\"" + name + "\"";
    }

    internal static object? Value(object? value) => value switch
    {
        null => null,
        byte number => (short)number,
        byte[] bytes => (byte[])bytes.Clone(),
        bool or sbyte or short or int or long or decimal or string or DateOnly or TimeOnly or
            DateTime or DateTimeOffset or TimeSpan or Guid => value,
        float number when float.IsFinite(number) => number,
        double number when double.IsFinite(number) => number,
        _ => throw new ArgumentException("SQL mapping parameters must be supported scalar values or byte arrays; floating-point values must be finite.", nameof(value)),
    };

    internal static DatabaseType ValueType(object? value) => value switch
    {
        null => DatabaseType.Null,
        bool => DatabaseType.Boolean,
        sbyte => DatabaseType.Int8,
        short => DatabaseType.Int16,
        int => DatabaseType.Int32,
        long => DatabaseType.Int64,
        float => DatabaseType.Float32,
        double => DatabaseType.Float64,
        decimal => DatabaseType.Decimal,
        string => DatabaseType.String,
        byte[] => DatabaseType.Binary,
        DateOnly => DatabaseType.Date,
        TimeOnly => DatabaseType.Time,
        DateTime => DatabaseType.DateTime,
        DateTimeOffset => DatabaseType.DateTimeOffset,
        TimeSpan => DatabaseType.TimeSpan,
        Guid => DatabaseType.Guid,
        _ => throw new ArgumentException("The value has no supported SQL mapping storage type.", nameof(value)),
    };
}

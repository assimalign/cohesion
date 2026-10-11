using System;
using System.Globalization;

using Assimalign.Cohesion.Database.Types;

// Deviates from the repo RootNamespace rule: this stateless source keeps its owning namespace
// when compiled into the SQL schema and SDK task assemblies (general-rules.md, Shared source).
namespace Assimalign.Cohesion.Database.Sql.Schema.Internal;

/// <summary>Renders CLR values once for retained declarations and build-time extraction.</summary>
internal static class SqlDefaultLiteralRenderer
{
    internal static string Render(DatabaseType columnType, object? value)
    {
        if (value is null)
        {
            return "NULL";
        }

        if (value is Enum enumeration)
        {
            value = Convert.ToDecimal(enumeration, CultureInfo.InvariantCulture);
        }

        string? literal = (columnType, value) switch
        {
            (DatabaseType.Boolean, bool boolean) => boolean ? "TRUE" : "FALSE",
            (DatabaseType.String, string text) => Quote(text),
            (DatabaseType.String, char character) => Quote(character.ToString()),
            (DatabaseType.Binary, byte[] bytes) => Quote("\\x" + Convert.ToHexString(bytes)),
            (DatabaseType.Int8, _) => Integer(value, sbyte.MinValue, sbyte.MaxValue),
            (DatabaseType.Int16, _) => Integer(value, short.MinValue, short.MaxValue),
            (DatabaseType.Int32, _) => Integer(value, int.MinValue, int.MaxValue),
            (DatabaseType.Int64, _) => Integer(value, long.MinValue, long.MaxValue),
            (DatabaseType.Decimal, decimal number) => number.ToString(CultureInfo.InvariantCulture),
            (DatabaseType.Decimal, _) when IsInteger(value) => Convert.ToDecimal(value, CultureInfo.InvariantCulture).ToString(CultureInfo.InvariantCulture),
            (DatabaseType.Float32, float number) when float.IsFinite(number) => number.ToString("R", CultureInfo.InvariantCulture),
            (DatabaseType.Float64, double number) when double.IsFinite(number) => number.ToString("R", CultureInfo.InvariantCulture),
            (DatabaseType.Float64, float number) when float.IsFinite(number) => ((double)number).ToString("R", CultureInfo.InvariantCulture),
            (DatabaseType.Date, DateOnly date) => Quote(date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)),
            (DatabaseType.Time, TimeOnly time) => Quote(time.ToString("HH:mm:ss.fffffff", CultureInfo.InvariantCulture)),
            (DatabaseType.DateTime, DateTime dateTime) => Quote(dateTime.ToString("yyyy-MM-dd'T'HH:mm:ss.fffffff", CultureInfo.InvariantCulture)),
            (DatabaseType.DateTimeOffset, DateTimeOffset dateTime) => Quote(dateTime.ToString("yyyy-MM-dd'T'HH:mm:ss.fffffffzzz", CultureInfo.InvariantCulture)),
            (DatabaseType.TimeSpan, TimeSpan duration) => Quote(duration.ToString("c", CultureInfo.InvariantCulture)),
            (DatabaseType.Guid, Guid identifier) => Quote(identifier.ToString("D")),
            _ => null
        };

        return literal ?? throw new ArgumentException(
            $"The default value cannot be represented as a {columnType} column literal.", nameof(value));
    }

    internal static DatabaseType StorageType(Type clrType)
    {
        Type valueType = Nullable.GetUnderlyingType(clrType) ?? clrType;
        bool isEnum = valueType.IsEnum;
        if (isEnum)
        {
            valueType = Enum.GetUnderlyingType(valueType);
        }

        return valueType == typeof(bool) ? DatabaseType.Boolean
            : valueType == typeof(sbyte) ? DatabaseType.Int8
            : valueType == typeof(byte) || valueType == typeof(short) ? DatabaseType.Int16
            : valueType == typeof(int) || isEnum && valueType == typeof(ushort) ? DatabaseType.Int32
            : valueType == typeof(long) || isEnum && valueType == typeof(uint) ? DatabaseType.Int64
            : valueType == typeof(decimal) || isEnum && valueType == typeof(ulong) ? DatabaseType.Decimal
            : valueType == typeof(float) ? DatabaseType.Float32
            : valueType == typeof(double) ? DatabaseType.Float64
            : valueType == typeof(string) || valueType == typeof(char) ? DatabaseType.String
            : valueType == typeof(byte[]) ? DatabaseType.Binary
            : valueType == typeof(DateOnly) ? DatabaseType.Date
            : valueType == typeof(TimeOnly) ? DatabaseType.Time
            : valueType == typeof(DateTime) ? DatabaseType.DateTime
            : valueType == typeof(DateTimeOffset) ? DatabaseType.DateTimeOffset
            : valueType == typeof(TimeSpan) ? DatabaseType.TimeSpan
            : valueType == typeof(Guid) ? DatabaseType.Guid
            : DatabaseType.Null;
    }

    private static bool IsInteger(object value)
        => value is sbyte or byte or short or ushort or int or uint or long or ulong;

    private static string? Integer(object value, decimal minimum, decimal maximum)
    {
        if (!IsInteger(value) && value is not decimal)
        {
            return null;
        }

        decimal number = Convert.ToDecimal(value, CultureInfo.InvariantCulture);
        return number >= minimum && number <= maximum && decimal.Truncate(number) == number
            ? number.ToString("0", CultureInfo.InvariantCulture)
            : null;
    }

    private static string Quote(string value)
        => "'" + value.Replace("'", "''", StringComparison.Ordinal) + "'";
}

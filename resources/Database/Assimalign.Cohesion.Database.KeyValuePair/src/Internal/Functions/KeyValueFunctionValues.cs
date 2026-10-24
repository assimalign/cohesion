using System;
using System.Globalization;

using Assimalign.Cohesion.Database.Types;

namespace Assimalign.Cohesion.Database.KeyValuePair.Internal;

internal static class KeyValueFunctionValues
{
    internal static bool TryGetType(object? value, out DatabaseType type)
    {
        type = value switch
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
            byte[] or ReadOnlyMemory<byte> or Memory<byte> => DatabaseType.Binary,
            DateOnly => DatabaseType.Date,
            TimeOnly => DatabaseType.Time,
            DateTime => DatabaseType.DateTime,
            DateTimeOffset => DatabaseType.DateTimeOffset,
            TimeSpan => DatabaseType.TimeSpan,
            Guid => DatabaseType.Guid,
            _ => (DatabaseType)byte.MaxValue,
        };
        return (byte)type <= (byte)DatabaseType.Guid;
    }

    internal static object? Snapshot(object? value, string parameterName)
    {
        if (!TryGetType(value, out _))
        {
            throw new ArgumentException(string.Format(CultureInfo.CurrentCulture,
                ErrorMessages.FunctionValueUnsupported, value!.GetType().Name), parameterName);
        }
        return value switch
        {
            byte[] bytes => bytes.Clone(),
            ReadOnlyMemory<byte> bytes => bytes.ToArray(),
            Memory<byte> bytes => bytes.ToArray(),
            _ => value,
        };
    }

    internal static ReadOnlyMemory<byte> Binary(object? value) => value switch
    {
        byte[] bytes => bytes,
        ReadOnlyMemory<byte> bytes => bytes,
        Memory<byte> bytes => bytes,
        _ => throw new InvalidCastException(ErrorMessages.FunctionBinaryRequired),
    };

    internal static T Read<T>(object? value)
    {
        _ = ClrType<T>();
        if (value is null && !CanBeNull<T>()) { throw new InvalidCastException(ErrorMessages.FunctionNullValueRequired); }
        if (typeof(T) == typeof(byte[]))
        {
            return value is null ? default! : (T)(object)Binary(value).ToArray();
        }
        if (typeof(T) == typeof(ReadOnlyMemory<byte>)) { return (T)(object)Binary(value); }
        if (typeof(T) == typeof(Memory<byte>)) { return (T)(object)Binary(value).ToArray().AsMemory(); }
        return (T)value!;
    }

    internal static bool CanBeNull<T>() => default(T) is null;

    internal static DatabaseType ClrType<T>()
    {
        Type type = typeof(T);
        if (type == typeof(bool) || type == typeof(bool?)) { return DatabaseType.Boolean; }
        if (type == typeof(sbyte) || type == typeof(sbyte?)) { return DatabaseType.Int8; }
        if (type == typeof(short) || type == typeof(short?)) { return DatabaseType.Int16; }
        if (type == typeof(int) || type == typeof(int?)) { return DatabaseType.Int32; }
        if (type == typeof(long) || type == typeof(long?)) { return DatabaseType.Int64; }
        if (type == typeof(float) || type == typeof(float?)) { return DatabaseType.Float32; }
        if (type == typeof(double) || type == typeof(double?)) { return DatabaseType.Float64; }
        if (type == typeof(decimal) || type == typeof(decimal?)) { return DatabaseType.Decimal; }
        if (type == typeof(string)) { return DatabaseType.String; }
        if (type == typeof(byte[]) || type == typeof(ReadOnlyMemory<byte>) || type == typeof(Memory<byte>)) { return DatabaseType.Binary; }
        if (type == typeof(DateOnly) || type == typeof(DateOnly?)) { return DatabaseType.Date; }
        if (type == typeof(TimeOnly) || type == typeof(TimeOnly?)) { return DatabaseType.Time; }
        if (type == typeof(DateTime) || type == typeof(DateTime?)) { return DatabaseType.DateTime; }
        if (type == typeof(DateTimeOffset) || type == typeof(DateTimeOffset?)) { return DatabaseType.DateTimeOffset; }
        if (type == typeof(TimeSpan) || type == typeof(TimeSpan?)) { return DatabaseType.TimeSpan; }
        if (type == typeof(Guid) || type == typeof(Guid?)) { return DatabaseType.Guid; }
        throw new NotSupportedException(string.Format(CultureInfo.CurrentCulture,
            ErrorMessages.FunctionClrTypeUnsupported, type.Name));
    }
}

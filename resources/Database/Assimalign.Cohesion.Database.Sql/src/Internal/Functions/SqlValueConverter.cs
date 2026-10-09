using System;
using System.Runtime.CompilerServices;

namespace Assimalign.Cohesion.Database.Sql.Internal;

/// <summary>
/// Maps one CLR type to its SQL type and converts between it and <see cref="SqlValue"/>, for the
/// typed function shorthands (<see cref="SqlScalarFunction.Create{T1, TResult}"/>,
/// <see cref="SqlAggregateFunction.Create{TState, T1, TResult}"/>).
/// </summary>
/// <remarks>
/// <para>
/// Every member branches on <c>typeof(T) == typeof(…)</c>. For a value type the instantiation is
/// compiled on its own, and NativeAOT folds each test to a constant, so a conversion is one
/// accessor call with no branch left; for a reference type the shared code tests the two reference
/// types first. Values move through <see cref="Unsafe.As{TFrom, TTo}(ref TFrom)"/> between types the
/// test proved identical: no reflection, no boxing, no dynamic code.
/// </para>
/// <para>
/// A nullable value type maps as its underlying type. A strict function never receives NULL, so a
/// non-nullable parameter is safe; a <see langword="null"/> result is SQL NULL.
/// </para>
/// </remarks>
/// <typeparam name="T">The CLR type.</typeparam>
internal static class SqlValueConverter<T>
{
    private static readonly SqlType? _type = Resolve();

    /// <summary>Gets the SQL type of <typeparamref name="T"/>.</summary>
    /// <exception cref="NotSupportedException"><typeparamref name="T"/> has no SQL type.</exception>
    internal static SqlType Type => _type ?? throw new NotSupportedException(
        $"The CLR type '{typeof(T).Name}' has no SQL type. A typed function takes and returns bool, sbyte, short, int, long, float, " +
        "double, decimal, string, byte[], DateOnly, TimeOnly, DateTime, DateTimeOffset, TimeSpan or Guid, or a nullable of a value type.");

    /// <summary>Reads an argument as <typeparamref name="T"/>.</summary>
    /// <param name="value">The argument, already of <see cref="Type"/>'s storage type, or NULL.</param>
    /// <returns>The value; <see langword="default"/> (null) for NULL of a reference or nullable type.</returns>
    /// <exception cref="InvalidCastException">The value is NULL and <typeparamref name="T"/> is a non-nullable value type, or the value is of another type.</exception>
    internal static T FromValue(in SqlValue value)
    {
        if (typeof(T) == typeof(string))
        {
            string? text = value.IsNull ? null : value.AsString();
            return Unsafe.As<string?, T>(ref text);
        }
        if (typeof(T) == typeof(byte[]))
        {
            byte[]? bytes = value.IsNull ? null : value.AsBinary();
            return Unsafe.As<byte[]?, T>(ref bytes);
        }
        if (typeof(T) == typeof(bool)) { bool v = value.AsBoolean(); return Unsafe.As<bool, T>(ref v); }
        if (typeof(T) == typeof(sbyte)) { sbyte v = value.AsSByte(); return Unsafe.As<sbyte, T>(ref v); }
        if (typeof(T) == typeof(short)) { short v = value.AsInt16(); return Unsafe.As<short, T>(ref v); }
        if (typeof(T) == typeof(int)) { int v = value.AsInt32(); return Unsafe.As<int, T>(ref v); }
        if (typeof(T) == typeof(long)) { long v = value.AsInt64(); return Unsafe.As<long, T>(ref v); }
        if (typeof(T) == typeof(float)) { float v = value.AsSingle(); return Unsafe.As<float, T>(ref v); }
        if (typeof(T) == typeof(double)) { double v = value.AsDouble(); return Unsafe.As<double, T>(ref v); }
        if (typeof(T) == typeof(decimal)) { decimal v = value.AsDecimal(); return Unsafe.As<decimal, T>(ref v); }
        if (typeof(T) == typeof(DateOnly)) { DateOnly v = value.AsDate(); return Unsafe.As<DateOnly, T>(ref v); }
        if (typeof(T) == typeof(TimeOnly)) { TimeOnly v = value.AsTime(); return Unsafe.As<TimeOnly, T>(ref v); }
        if (typeof(T) == typeof(DateTime)) { DateTime v = value.AsDateTime(); return Unsafe.As<DateTime, T>(ref v); }
        if (typeof(T) == typeof(DateTimeOffset)) { DateTimeOffset v = value.AsDateTimeOffset(); return Unsafe.As<DateTimeOffset, T>(ref v); }
        if (typeof(T) == typeof(TimeSpan)) { TimeSpan v = value.AsTimeSpan(); return Unsafe.As<TimeSpan, T>(ref v); }
        if (typeof(T) == typeof(Guid)) { Guid v = value.AsGuid(); return Unsafe.As<Guid, T>(ref v); }
        if (typeof(T) == typeof(bool?)) { bool? v = value.IsNull ? null : value.AsBoolean(); return Unsafe.As<bool?, T>(ref v); }
        if (typeof(T) == typeof(sbyte?)) { sbyte? v = value.IsNull ? null : value.AsSByte(); return Unsafe.As<sbyte?, T>(ref v); }
        if (typeof(T) == typeof(short?)) { short? v = value.IsNull ? null : value.AsInt16(); return Unsafe.As<short?, T>(ref v); }
        if (typeof(T) == typeof(int?)) { int? v = value.IsNull ? null : value.AsInt32(); return Unsafe.As<int?, T>(ref v); }
        if (typeof(T) == typeof(long?)) { long? v = value.IsNull ? null : value.AsInt64(); return Unsafe.As<long?, T>(ref v); }
        if (typeof(T) == typeof(float?)) { float? v = value.IsNull ? null : value.AsSingle(); return Unsafe.As<float?, T>(ref v); }
        if (typeof(T) == typeof(double?)) { double? v = value.IsNull ? null : value.AsDouble(); return Unsafe.As<double?, T>(ref v); }
        if (typeof(T) == typeof(decimal?)) { decimal? v = value.IsNull ? null : value.AsDecimal(); return Unsafe.As<decimal?, T>(ref v); }
        if (typeof(T) == typeof(DateOnly?)) { DateOnly? v = value.IsNull ? null : value.AsDate(); return Unsafe.As<DateOnly?, T>(ref v); }
        if (typeof(T) == typeof(TimeOnly?)) { TimeOnly? v = value.IsNull ? null : value.AsTime(); return Unsafe.As<TimeOnly?, T>(ref v); }
        if (typeof(T) == typeof(DateTime?)) { DateTime? v = value.IsNull ? null : value.AsDateTime(); return Unsafe.As<DateTime?, T>(ref v); }
        if (typeof(T) == typeof(DateTimeOffset?)) { DateTimeOffset? v = value.IsNull ? null : value.AsDateTimeOffset(); return Unsafe.As<DateTimeOffset?, T>(ref v); }
        if (typeof(T) == typeof(TimeSpan?)) { TimeSpan? v = value.IsNull ? null : value.AsTimeSpan(); return Unsafe.As<TimeSpan?, T>(ref v); }
        if (typeof(T) == typeof(Guid?)) { Guid? v = value.IsNull ? null : value.AsGuid(); return Unsafe.As<Guid?, T>(ref v); }

        _ = Type;
        throw new NotSupportedException();
    }

    /// <summary>Converts a result to a SQL value.</summary>
    /// <param name="value">The result.</param>
    /// <returns>The SQL value; NULL for a <see langword="null"/> result.</returns>
    internal static SqlValue ToValue(T value)
    {
        if (typeof(T) == typeof(string)) { return SqlValue.FromString(Unsafe.As<T, string?>(ref value)); }
        if (typeof(T) == typeof(byte[])) { return SqlValue.FromBinary(Unsafe.As<T, byte[]?>(ref value)); }
        if (typeof(T) == typeof(bool)) { return SqlValue.FromBoolean(Unsafe.As<T, bool>(ref value)); }
        if (typeof(T) == typeof(sbyte)) { return SqlValue.FromSByte(Unsafe.As<T, sbyte>(ref value)); }
        if (typeof(T) == typeof(short)) { return SqlValue.FromInt16(Unsafe.As<T, short>(ref value)); }
        if (typeof(T) == typeof(int)) { return SqlValue.FromInt32(Unsafe.As<T, int>(ref value)); }
        if (typeof(T) == typeof(long)) { return SqlValue.FromInt64(Unsafe.As<T, long>(ref value)); }
        if (typeof(T) == typeof(float)) { return SqlValue.FromSingle(Unsafe.As<T, float>(ref value)); }
        if (typeof(T) == typeof(double)) { return SqlValue.FromDouble(Unsafe.As<T, double>(ref value)); }
        if (typeof(T) == typeof(decimal)) { return SqlValue.FromDecimal(Unsafe.As<T, decimal>(ref value)); }
        if (typeof(T) == typeof(DateOnly)) { return SqlValue.FromDate(Unsafe.As<T, DateOnly>(ref value)); }
        if (typeof(T) == typeof(TimeOnly)) { return SqlValue.FromTime(Unsafe.As<T, TimeOnly>(ref value)); }
        if (typeof(T) == typeof(DateTime)) { return SqlValue.FromDateTime(Unsafe.As<T, DateTime>(ref value)); }
        if (typeof(T) == typeof(DateTimeOffset)) { return SqlValue.FromDateTimeOffset(Unsafe.As<T, DateTimeOffset>(ref value)); }
        if (typeof(T) == typeof(TimeSpan)) { return SqlValue.FromTimeSpan(Unsafe.As<T, TimeSpan>(ref value)); }
        if (typeof(T) == typeof(Guid)) { return SqlValue.FromGuid(Unsafe.As<T, Guid>(ref value)); }
        if (typeof(T) == typeof(bool?)) { return Unsafe.As<T, bool?>(ref value) is { } v ? SqlValue.FromBoolean(v) : SqlValue.Null; }
        if (typeof(T) == typeof(sbyte?)) { return Unsafe.As<T, sbyte?>(ref value) is { } v ? SqlValue.FromSByte(v) : SqlValue.Null; }
        if (typeof(T) == typeof(short?)) { return Unsafe.As<T, short?>(ref value) is { } v ? SqlValue.FromInt16(v) : SqlValue.Null; }
        if (typeof(T) == typeof(int?)) { return Unsafe.As<T, int?>(ref value) is { } v ? SqlValue.FromInt32(v) : SqlValue.Null; }
        if (typeof(T) == typeof(long?)) { return Unsafe.As<T, long?>(ref value) is { } v ? SqlValue.FromInt64(v) : SqlValue.Null; }
        if (typeof(T) == typeof(float?)) { return Unsafe.As<T, float?>(ref value) is { } v ? SqlValue.FromSingle(v) : SqlValue.Null; }
        if (typeof(T) == typeof(double?)) { return Unsafe.As<T, double?>(ref value) is { } v ? SqlValue.FromDouble(v) : SqlValue.Null; }
        if (typeof(T) == typeof(decimal?)) { return Unsafe.As<T, decimal?>(ref value) is { } v ? SqlValue.FromDecimal(v) : SqlValue.Null; }
        if (typeof(T) == typeof(DateOnly?)) { return Unsafe.As<T, DateOnly?>(ref value) is { } v ? SqlValue.FromDate(v) : SqlValue.Null; }
        if (typeof(T) == typeof(TimeOnly?)) { return Unsafe.As<T, TimeOnly?>(ref value) is { } v ? SqlValue.FromTime(v) : SqlValue.Null; }
        if (typeof(T) == typeof(DateTime?)) { return Unsafe.As<T, DateTime?>(ref value) is { } v ? SqlValue.FromDateTime(v) : SqlValue.Null; }
        if (typeof(T) == typeof(DateTimeOffset?)) { return Unsafe.As<T, DateTimeOffset?>(ref value) is { } v ? SqlValue.FromDateTimeOffset(v) : SqlValue.Null; }
        if (typeof(T) == typeof(TimeSpan?)) { return Unsafe.As<T, TimeSpan?>(ref value) is { } v ? SqlValue.FromTimeSpan(v) : SqlValue.Null; }
        if (typeof(T) == typeof(Guid?)) { return Unsafe.As<T, Guid?>(ref value) is { } v ? SqlValue.FromGuid(v) : SqlValue.Null; }

        _ = Type;
        throw new NotSupportedException();
    }

    private static SqlType? Resolve()
    {
        if (typeof(T) == typeof(string)) { return SqlType.Text; }
        if (typeof(T) == typeof(byte[])) { return SqlType.Binary; }
        if (typeof(T) == typeof(bool) || typeof(T) == typeof(bool?)) { return SqlType.Boolean; }
        if (typeof(T) == typeof(sbyte) || typeof(T) == typeof(sbyte?)) { return SqlType.TinyInt; }
        if (typeof(T) == typeof(short) || typeof(T) == typeof(short?)) { return SqlType.SmallInt; }
        if (typeof(T) == typeof(int) || typeof(T) == typeof(int?)) { return SqlType.Integer; }
        if (typeof(T) == typeof(long) || typeof(T) == typeof(long?)) { return SqlType.BigInt; }
        if (typeof(T) == typeof(float) || typeof(T) == typeof(float?)) { return SqlType.Real; }
        if (typeof(T) == typeof(double) || typeof(T) == typeof(double?)) { return SqlType.Double; }
        if (typeof(T) == typeof(decimal) || typeof(T) == typeof(decimal?)) { return SqlType.Numeric; }
        if (typeof(T) == typeof(DateOnly) || typeof(T) == typeof(DateOnly?)) { return SqlType.Date; }
        if (typeof(T) == typeof(TimeOnly) || typeof(T) == typeof(TimeOnly?)) { return SqlType.Time; }
        if (typeof(T) == typeof(DateTime) || typeof(T) == typeof(DateTime?)) { return SqlType.Timestamp; }
        if (typeof(T) == typeof(DateTimeOffset) || typeof(T) == typeof(DateTimeOffset?)) { return SqlType.TimestampTz; }
        if (typeof(T) == typeof(TimeSpan) || typeof(T) == typeof(TimeSpan?)) { return SqlType.Interval; }
        if (typeof(T) == typeof(Guid) || typeof(T) == typeof(Guid?)) { return SqlType.Uuid; }
        return null;
    }
}

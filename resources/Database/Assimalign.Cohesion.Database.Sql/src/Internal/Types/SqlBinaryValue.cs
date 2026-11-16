using System.Runtime.CompilerServices;

namespace Assimalign.Cohesion.Database.Sql.Internal;

/// <summary>Delivers a value whose producer can retain or reuse its binary array.</summary>
internal static class SqlBinaryValue
{
    private static readonly ConditionalWeakTable<byte[], object> _applicationValues = new();
    private static readonly object _applicationValue = new();

    internal static object? FromFunction(object? value, SqlFunction function)
    {
        if (value is byte[] bytes && function is not (SqlBuiltinScalarFunction or SqlUpperFunction or SqlLowerFunction or SqlLengthFunction or SqlAbsFunction or SqlExpressionFunction
            or SqlCountFunction or SqlExtremumFunction or SqlSumFunction or SqlStatisticsFunction or SqlStringAggregateFunction))
        { _applicationValues.GetValue(bytes, static _ => _applicationValue); }
        return value;
    }

    internal static bool IsApplicationValue(byte[] value) => _applicationValues.TryGetValue(value, out _);

    // Clone also gives an empty binary value its own array.
    internal static object? CopyShared(object? value)
    {
        if (value is not byte[] bytes) { return value; }
        var copy = (byte[])bytes.Clone();
        if (IsApplicationValue(bytes)) { _applicationValues.Add(copy, _applicationValue); }
        return copy;
    }
}

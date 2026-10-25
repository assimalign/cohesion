using System;
using System.Text.Json;

using Assimalign.Cohesion.Database.Types;

namespace Assimalign.Cohesion.Database.Documents.Internal;

internal static class DocumentTypedValue<T>
{
    internal static DatabaseType Type => ResolveType();
    internal static DocumentFunctionArgumentKind Kind => Type switch
    {
        DatabaseType.Boolean => DocumentFunctionArgumentKind.Boolean,
        DatabaseType.Int64 or DatabaseType.Decimal => DocumentFunctionArgumentKind.Number,
        DatabaseType.String => DocumentFunctionArgumentKind.String,
        DatabaseType.Json => DocumentFunctionArgumentKind.Json,
        _ => DocumentFunctionArgumentKind.Any,
    };
    private static DatabaseType ResolveType()
    {
        if (typeof(T) == typeof(DocumentValue) || typeof(T) == typeof(JsonElement)) { return typeof(T) == typeof(JsonElement) ? DatabaseType.Json : DatabaseType.Null; }
        if (typeof(T) == typeof(string)) { return DatabaseType.String; }
        if (typeof(T) == typeof(bool) || typeof(T) == typeof(bool?)) { return DatabaseType.Boolean; }
        if (typeof(T) == typeof(long) || typeof(T) == typeof(long?)) { return DatabaseType.Int64; }
        if (typeof(T) == typeof(int) || typeof(T) == typeof(int?) || typeof(T) == typeof(decimal) || typeof(T) == typeof(decimal?)) { return DatabaseType.Decimal; }
        throw new NotSupportedException(ErrorMessages.FunctionClrTypeInvalid);
    }
    internal static void RequireNullable(DocumentNullBehavior behavior)
    {
        if (behavior == DocumentNullBehavior.CalledOnNullOrMissingInput && default(T) is not null && typeof(T) != typeof(DocumentValue))
        { throw new ArgumentException(ErrorMessages.FunctionNullableInputRequired, nameof(behavior)); }
    }
    internal static T Read(DocumentValue value)
    {
        // These are adapter-owned input conversions, before the application delegate runs.
        // Do not catch around the delegate: an application's own overflow remains CD05017.
        try { return ReadCore(value); }
        catch (InvalidCastException error)
        { throw new DocumentPlanningException("CD02008", ErrorMessages.FunctionValueInvalid, innerException: error); }
        catch (OverflowException error)
        { throw new DocumentPlanningException("CD02007", ErrorMessages.ExpressionEvaluatorEvaluateFailure2, innerException: error); }
    }
    private static T ReadCore(DocumentValue value)
    {
        if (typeof(T) == typeof(DocumentValue)) { return (T)(object)value; }
        if (value.IsNull || value.IsMissing) { return default!; }
        object converted = Type switch
        {
            DatabaseType.Boolean => value.AsBoolean(),
            DatabaseType.Int64 => value.AsInt64(),
            DatabaseType.Decimal when typeof(T) == typeof(int) || typeof(T) == typeof(int?) => checked((int)value.AsInt64()),
            DatabaseType.Decimal => value.AsDecimal(),
            DatabaseType.String => value.AsString(),
            DatabaseType.Json => value.AsJson(),
            _ => throw new NotSupportedException(ErrorMessages.FunctionClrTypeInvalid),
        };
        return (T)converted;
    }
    internal static DocumentValue Write(T value)
    {
        if (value is null) { return DocumentValue.Null; }
        if (value is DocumentValue document) { return document; }
        if (value is JsonElement json) { return DocumentValue.FromJson(json); }
        if (value is long integer) { return DocumentValue.FromInt64(integer); }
        return new(DocumentExpressionEvaluator.Normalize(value));
    }
}

using System;
using System.Text.Json;

using Assimalign.Cohesion.Database.Documents.Internal;
using Assimalign.Cohesion.Database.Types;

namespace Assimalign.Cohesion.Database.Documents;

/// <summary>A document function value, preserving MISSING separately from NULL.</summary>
/// <remarks>FromJson copies its input. Do not retain borrowed call arguments beyond the call.</remarks>
[global::System.Diagnostics.DebuggerDisplay("DocumentValue {Type}")]
[global::System.Diagnostics.DebuggerTypeProxy(typeof(DocumentValue.DebugView))]
public readonly struct DocumentValue
{
    private readonly object? _value;

    internal DocumentValue(object? value) { _value = value; }
    /// <summary>Gets NULL, which is also the default value.</summary>
    public static DocumentValue Null => default;
    /// <summary>Gets MISSING.</summary>
    public static DocumentValue Missing => new(DocumentMissing.Instance);
    /// <summary>Gets whether this value is MISSING.</summary>
    public bool IsMissing => _value is DocumentMissing;
    /// <summary>Gets whether this value is NULL.</summary>
    public bool IsNull => _value is null;
    /// <summary>Gets the storage type; NULL and MISSING use Null.</summary>
    public DatabaseType Type => _value switch
    {
        null or DocumentMissing => DatabaseType.Null,
        bool => DatabaseType.Boolean, long => DatabaseType.Int64, decimal => DatabaseType.Decimal,
        string => DatabaseType.String, JsonElement => DatabaseType.Json,
        _ => throw new InvalidOperationException(ErrorMessages.FunctionValueInvalid),
    };
    internal object? Value => _value;

    /// <summary>Creates a Boolean.</summary><param name="value">The value.</param><returns>The function value.</returns>
    public static DocumentValue FromBoolean(bool value) => new(value);
    /// <summary>Creates an Int64.</summary><param name="value">The value.</param><returns>The function value.</returns>
    public static DocumentValue FromInt64(long value) => new(value);
    /// <summary>Creates a decimal.</summary><param name="value">The value.</param><returns>The function value.</returns>
    public static DocumentValue FromDecimal(decimal value) => new(value);
    /// <summary>Creates a string or NULL.</summary><param name="value">The value.</param><returns>The function value.</returns>
    public static DocumentValue FromString(string? value) => new(value);
    /// <summary>Copies and normalizes JSON.</summary><param name="value">The JSON.</param><returns>The owned value.</returns>
    /// <exception cref="DatabaseException">A JSON number exceeds the numeric domain.</exception>
    public static DocumentValue FromJson(JsonElement value) => new(DocumentExpressionEvaluator.Normalize(value.Clone()));
    /// <summary>Reads a Boolean.</summary><returns>The Boolean.</returns><exception cref="InvalidCastException">The value has another kind.</exception>
    public bool AsBoolean() => _value is bool value ? value : throw new InvalidCastException(ErrorMessages.FunctionValueInvalid);
    /// <summary>Reads an integral Int64.</summary><returns>The integer.</returns><exception cref="InvalidCastException">The value is not integral.</exception><exception cref="OverflowException">The number exceeds Int64.</exception>
    public long AsInt64() => _value switch
    {
        long value => value,
        decimal value when decimal.Truncate(value) == value => checked((long)value),
        _ => throw new InvalidCastException(ErrorMessages.FunctionValueInvalid),
    };
    /// <summary>Reads a number.</summary><returns>The decimal.</returns><exception cref="InvalidCastException">The value is not numeric.</exception>
    public decimal AsDecimal() => _value switch { decimal value => value, long value => value, _ => throw new InvalidCastException(ErrorMessages.FunctionValueInvalid) };
    /// <summary>Reads a string.</summary><returns>The string.</returns><exception cref="InvalidCastException">The value is not a string.</exception>
    public string AsString() => _value as string ?? throw new InvalidCastException(ErrorMessages.FunctionValueInvalid);
    /// <summary>Reads a borrowed JSON array or object.</summary><returns>The JSON.</returns><exception cref="InvalidCastException">The value is not an array or object.</exception>
    public JsonElement AsJson() => _value is JsonElement value ? value : throw new InvalidCastException(ErrorMessages.FunctionValueInvalid);

    private sealed class DebugView
    {
        private readonly DocumentValue _target;
        public DebugView(DocumentValue value) { _target = value; }
        public DatabaseType Type => _target.Type;
        public bool IsMissing => _target.IsMissing;
        public bool IsNull => _target.IsNull;
    }
}

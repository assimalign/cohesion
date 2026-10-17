using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using System.Text.Json;

using Assimalign.Cohesion.Database.Execution;

namespace Assimalign.Cohesion.Database.Documents.Client;

/// <summary>A projection row with ordinal and column-name access.</summary>
public sealed class DocumentRow : DatabaseRow, IReadOnlyList<object?>
{
    private readonly object?[] _values;
    private readonly IReadOnlyDictionary<string, int> _ordinals;

    internal DocumentRow(object?[] values, IReadOnlyDictionary<string, int> ordinals) : base(supportsMissing: true)
    {
        _values = values;
        _ordinals = ordinals;
    }

    /// <summary>Gets the number of values.</summary>
    public int Count => _values.Length;

    /// <summary>Gets a value by ordinal.</summary>
    /// <param name="index">The zero-based column ordinal.</param>
    /// <returns>The boxed scalar, JSON value or null.</returns>
    /// <exception cref="IndexOutOfRangeException">The ordinal is outside the row.</exception>
    public object? this[int index] => GetValue(index);

    /// <summary>Gets a value by column name; the first duplicate name wins.</summary>
    /// <param name="name">The exact column name.</param>
    /// <returns>The boxed scalar, JSON value or null.</returns>
    /// <exception cref="ArgumentNullException">The name is null.</exception>
    /// <exception cref="KeyNotFoundException">No column has that name.</exception>
    public object? this[string name] => GetValue(_ordinals[name]);

    /// <inheritdoc />
    public override int FieldCount => _values.Length;

    /// <inheritdoc />
    public override bool IsNull(int ordinal) => _values[ordinal] is null or JsonElement { ValueKind: JsonValueKind.Null };

    /// <inheritdoc />
    protected override bool IsMissingCore(int ordinal) => _values[ordinal] is JsonElement { ValueKind: JsonValueKind.Undefined };

    /// <inheritdoc />
    public override object? GetValue(int ordinal) => _values[ordinal] is JsonElement { ValueKind: JsonValueKind.Undefined } ? null : _values[ordinal];

    /// <inheritdoc />
    public override string? GetString(int ordinal) => GetValue(ordinal) switch
    {
        null => null,
        JsonElement json => json.GetRawText(),
        var value => Convert.ToString(value, CultureInfo.InvariantCulture),
    };

    /// <inheritdoc />
    public override ReadOnlyMemory<byte> GetBytes(int ordinal) => GetValue(ordinal) switch
    {
        null => ReadOnlyMemory<byte>.Empty,
        JsonElement json => Encoding.UTF8.GetBytes(json.GetRawText()),
        string text => Encoding.UTF8.GetBytes(text),
        _ => throw new InvalidOperationException(ErrorMessages.RowBytesInvalid),
    };

    /// <inheritdoc />
    public override int GetInt32(int ordinal) => Convert.ToInt32(Required(ordinal), CultureInfo.InvariantCulture);

    /// <inheritdoc />
    public override long GetInt64(int ordinal) => Convert.ToInt64(Required(ordinal), CultureInfo.InvariantCulture);

    /// <inheritdoc />
    public override bool GetBoolean(int ordinal) => Convert.ToBoolean(Required(ordinal), CultureInfo.InvariantCulture);

    /// <inheritdoc />
    public override double GetDouble(int ordinal) => Convert.ToDouble(Required(ordinal), CultureInfo.InvariantCulture);

    private object Required(int ordinal) => GetValue(ordinal) ?? throw new InvalidOperationException(ErrorMessages.RowValueAbsent);

    /// <inheritdoc />
    public IEnumerator<object?> GetEnumerator()
    {
        for (int index = 0; index < Count; index++) { yield return GetValue(index); }
    }

    IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
}

using System;
using System.Globalization;

using Assimalign.Cohesion.Database.Execution;

namespace Assimalign.Cohesion.Database.KeyValuePair.Internal;

/// <summary>
/// A row over evaluated values with converting typed accessors.
/// </summary>
[global::System.Diagnostics.DebuggerDisplay("KeyValueMaterializedRow FieldCount={FieldCount}")]
[global::System.Diagnostics.DebuggerTypeProxy(typeof(global::Assimalign.Cohesion.Database.KeyValuePair.Internal.KeyValueMaterializedRow.DebugView))]
internal sealed class KeyValueMaterializedRow : DatabaseRow
{
    private readonly object?[] _values;

    internal KeyValueMaterializedRow(object?[] values)
    {
        _values = values;
    }

    /// <inheritdoc />
    public override int FieldCount => _values.Length;

    /// <inheritdoc />
    public override bool IsNull(int ordinal) => _values[ordinal] is null;

    /// <inheritdoc />
    public override ReadOnlyMemory<byte> GetBytes(int ordinal) => _values[ordinal] switch
    {
        null => ReadOnlyMemory<byte>.Empty,
        byte[] bytes => bytes,
        _ => throw DatabaseException.CreateKeyValuePairFailure("CK02001", string.Format(System.Globalization.CultureInfo.CurrentCulture, ErrorMessages.ValueMaterializedRowGetBytesDatabase, ordinal)),
    };

    /// <inheritdoc />
    public override string? GetString(int ordinal) => _values[ordinal] switch
    {
        null => null,
        string text => text,
        var value => Convert.ToString(value, CultureInfo.InvariantCulture),
    };

    /// <inheritdoc />
    public override int GetInt32(int ordinal) => Convert.ToInt32(RequireValue(ordinal), CultureInfo.InvariantCulture);

    /// <inheritdoc />
    public override long GetInt64(int ordinal) => Convert.ToInt64(RequireValue(ordinal), CultureInfo.InvariantCulture);

    /// <inheritdoc />
    public override bool GetBoolean(int ordinal) => Convert.ToBoolean(RequireValue(ordinal), CultureInfo.InvariantCulture);

    /// <inheritdoc />
    public override double GetDouble(int ordinal) => Convert.ToDouble(RequireValue(ordinal), CultureInfo.InvariantCulture);

    /// <inheritdoc />
    public override object? GetValue(int ordinal) => _values[ordinal];

    private object RequireValue(int ordinal)
        => _values[ordinal] ?? throw DatabaseException.CreateKeyValuePairFailure("CK02002", string.Format(System.Globalization.CultureInfo.CurrentCulture, ErrorMessages.MaterializedRowRequireValueDatabase, ordinal));

    private sealed class DebugView
    {
        private readonly global::Assimalign.Cohesion.Database.KeyValuePair.Internal.KeyValueMaterializedRow _target;

        public DebugView(global::Assimalign.Cohesion.Database.KeyValuePair.Internal.KeyValueMaterializedRow value)
        {
            _target = value;
        }

        public int FieldCount => _target.FieldCount;
    }
}

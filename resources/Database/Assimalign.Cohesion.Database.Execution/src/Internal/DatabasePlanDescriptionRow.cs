using System;
using System.Globalization;
using System.Text;

namespace Assimalign.Cohesion.Database.Execution.Internal;

internal sealed class DatabasePlanDescriptionRow : DatabaseRow
{
    private readonly object?[] _values;

    internal DatabasePlanDescriptionRow(DatabasePlanNode node)
    {
        ArgumentNullException.ThrowIfNull(node);
        _values = [node.NodeId, node.ParentId, node.Operator, node.Target, node.Index, node.Detail, node.EstimatedRows, node.ActualRows];
    }

    public override int FieldCount => _values.Length;
    public override bool IsNull(int ordinal) => _values[ordinal] is null;
    public override object? GetValue(int ordinal) => _values[ordinal];
    public override string? GetString(int ordinal) => _values[ordinal] is null ? null : Convert.ToString(_values[ordinal], CultureInfo.InvariantCulture);
    public override int GetInt32(int ordinal) => Convert.ToInt32(Required(ordinal), CultureInfo.InvariantCulture);
    public override long GetInt64(int ordinal) => Convert.ToInt64(Required(ordinal), CultureInfo.InvariantCulture);
    public override bool GetBoolean(int ordinal) => Convert.ToBoolean(Required(ordinal), CultureInfo.InvariantCulture);
    public override double GetDouble(int ordinal) => Convert.ToDouble(Required(ordinal), CultureInfo.InvariantCulture);
    public override ReadOnlyMemory<byte> GetBytes(int ordinal) => _values[ordinal] is null
        ? ReadOnlyMemory<byte>.Empty : Encoding.UTF8.GetBytes(GetString(ordinal)!);

    private object Required(int ordinal) => _values[ordinal] ?? throw new InvalidOperationException();
}

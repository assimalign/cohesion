using System;
using System.Globalization;
using System.Text;

namespace Assimalign.Cohesion.Database.Execution.Internal;

[global::System.Diagnostics.DebuggerDisplay("DatabasePlanDescriptionRow Fields = {FieldCount}")]
[global::System.Diagnostics.DebuggerTypeProxy(typeof(global::Assimalign.Cohesion.Database.Execution.Internal.DatabasePlanDescriptionRow.DebugView))]
internal sealed class DatabasePlanDescriptionRow : DatabaseRow
{
    private readonly object?[] _values;

    internal DatabasePlanDescriptionRow(DatabasePlanNode node)
    {
        ArgumentNullException.ThrowIfNull(node);
        _values = [node.NodeId, node.ParentId, node.Operator, node.Target, node.Index, node.Detail, node.EstimatedRows, node.ActualRows];
    }

    public override int FieldCount => _values.Length;
    protected override bool IsNullCore(int ordinal) => _values[ordinal] is null;
    protected override object? GetValueCore(int ordinal) => _values[ordinal];
    protected override string? GetStringCore(int ordinal) => _values[ordinal] is null ? null : Convert.ToString(_values[ordinal], CultureInfo.InvariantCulture);
    protected override int GetInt32Core(int ordinal) => Convert.ToInt32(Required(ordinal), CultureInfo.InvariantCulture);
    protected override long GetInt64Core(int ordinal) => Convert.ToInt64(Required(ordinal), CultureInfo.InvariantCulture);
    protected override bool GetBooleanCore(int ordinal) => Convert.ToBoolean(Required(ordinal), CultureInfo.InvariantCulture);
    protected override double GetDoubleCore(int ordinal) => Convert.ToDouble(Required(ordinal), CultureInfo.InvariantCulture);
    protected override ReadOnlyMemory<byte> GetBytesCore(int ordinal) => _values[ordinal] is null
        ? ReadOnlyMemory<byte>.Empty : Encoding.UTF8.GetBytes(GetStringCore(ordinal)!);

    private object Required(int ordinal) => _values[ordinal] ?? throw new InvalidOperationException();

    private sealed class DebugView
    {
        private readonly global::Assimalign.Cohesion.Database.Execution.Internal.DatabasePlanDescriptionRow _target;

        public DebugView(global::Assimalign.Cohesion.Database.Execution.Internal.DatabasePlanDescriptionRow value)
        {
            _target = value;
        }

        public int FieldCount => _target.FieldCount;
        public bool SupportsMissing => _target.SupportsMissing;
    }
}

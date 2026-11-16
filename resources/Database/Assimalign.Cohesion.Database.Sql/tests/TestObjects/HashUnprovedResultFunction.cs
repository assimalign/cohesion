using Assimalign.Cohesion.Database.Sql;

namespace Assimalign.Cohesion.Database.Sql.Tests.TestObjects;

internal sealed class HashUnprovedResultFunction : SqlScalarFunction
{
    private readonly bool _unknown;
    private readonly bool _text;
    internal HashUnprovedResultFunction(bool unknown, bool text = false)
        : base("unproved_result", [SqlType.BigInt, SqlType.AnyElement], unknown ? SqlType.AnyElement : SqlType.Decimal(2, 0),
            nullBehavior: SqlNullBehavior.CalledOnNullInput)
    { _unknown = unknown; _text = text; }
    protected override SqlValue InvokeCore(scoped in SqlArguments arguments)
    {
        long id = arguments.GetInt64(0);
        if (_text) { return id == 500 ? SqlValue.FromString(new string('x', 100)) : SqlValue.FromInt32(1); }
        return _unknown ? SqlValue.FromDouble(id == 500 ? 1e300 : 1.0)
            : SqlValue.FromDecimal(id % 300 == 250 ? decimal.MaxValue : 1m);
    }
}

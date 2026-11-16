namespace Assimalign.Cohesion.Database.Sql.Tests.TestObjects;

/// <summary>A polymorphic declaration whose runtime family deliberately violates its typed input.</summary>
internal sealed class UnprovedBooleanResultFunction : SqlScalarFunction
{
    internal UnprovedBooleanResultFunction()
        : base("unproved_boolean", [SqlType.AnyElement], SqlType.AnyElement, nullBehavior: SqlNullBehavior.CalledOnNullInput)
    { }

    protected override SqlValue InvokeCore(scoped in SqlArguments arguments) => SqlValue.FromBoolean(true);
}

namespace Assimalign.Cohesion.Database.Sql.Tests.TestObjects;

/// <summary>Observes the value family a grouped alternative delivers through the native ABI.</summary>
internal sealed class AlternativeValueTypeFunction : SqlScalarFunction
{
    internal AlternativeValueTypeFunction()
        : base("alternative_value_type", [SqlType.AnyElement], SqlType.Text, nullBehavior: SqlNullBehavior.CalledOnNullInput)
    { }

    protected override SqlValue InvokeCore(scoped in SqlArguments arguments) => SqlValue.FromString(arguments[0].Type.ToString());
}

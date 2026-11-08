namespace Assimalign.Cohesion.Database.Sql.Tests.TestObjects;

/// <summary>A native polymorphic result keeps the runtime incomparable-sort guard exercised.</summary>
internal sealed class MixedSortKeyFunction : SqlScalarFunction
{
    internal MixedSortKeyFunction()
        : base("mixed_sort_key", [SqlType.BigInt, SqlType.AnyElement], SqlType.AnyElement,
            nullBehavior: SqlNullBehavior.CalledOnNullInput)
    { }

    protected override SqlValue InvokeCore(scoped in SqlArguments arguments)
    {
        long id = arguments.GetInt64(0);
        return id == 1 ? SqlValue.FromString("a") : SqlValue.FromInt64(id);
    }
}

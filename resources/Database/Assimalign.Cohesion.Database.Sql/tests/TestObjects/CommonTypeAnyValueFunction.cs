namespace Assimalign.Cohesion.Database.Sql.Tests.TestObjects;

/// <summary>An unresolved application aggregate whose non-NULL result exposes row-time coercion.</summary>
internal sealed class CommonTypeAnyValueFunction : SqlAggregateFunction
{
    internal CommonTypeAnyValueFunction()
        : base("any_value", [SqlType.AnyElement], SqlType.AnyElement)
    {
    }

    protected override SqlAggregateAccumulator CreateAccumulatorCore(scoped in SqlFunctionContext context) => new Accumulator();

    private sealed class Accumulator : SqlAggregateAccumulator
    {
        protected override void AddCore(scoped in SqlArguments arguments) { }

        protected override SqlValue FinishCore() => SqlValue.FromDouble(1.0);
    }
}

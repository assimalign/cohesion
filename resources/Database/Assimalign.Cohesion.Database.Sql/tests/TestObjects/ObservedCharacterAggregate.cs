using System.Linq;

namespace Assimalign.Cohesion.Database.Sql.Tests.TestObjects;

/// <summary>Returns exactly the padded arguments the native aggregate receives.</summary>
internal sealed class ObservedCharacterAggregate : SqlAggregateFunction
{
    internal ObservedCharacterAggregate(int arity, int length)
        : base("observed_char", Enumerable.Repeat(SqlType.Char(length), arity).ToArray(), SqlType.Text)
    {
    }

    protected override SqlAggregateAccumulator CreateAccumulatorCore(scoped in SqlFunctionContext context) => new Accumulator();

    private sealed class Accumulator : SqlAggregateAccumulator
    {
        private string? _observed;

        protected override void AddCore(scoped in SqlArguments arguments)
        {
            var values = new string[arguments.Count];
            for (int index = 0; index < values.Length; index++)
            {
                values[index] = arguments.GetString(index);
            }
            _observed = string.Join("|", values);
        }

        protected override SqlValue FinishCore() => _observed is null ? SqlValue.Null : SqlValue.FromString(_observed);
    }
}

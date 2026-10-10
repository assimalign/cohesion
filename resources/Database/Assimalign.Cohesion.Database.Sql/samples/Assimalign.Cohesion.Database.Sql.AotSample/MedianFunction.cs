using System.Collections.Generic;

namespace Assimalign.Cohesion.Database.Sql.AotSample;

/// <summary>
/// A hand-written aggregate leaf, the design's <c>median(DOUBLE)</c>: one accumulator per group, which
/// a strict aggregate never hands a NULL argument; an empty group's median is NULL.
/// </summary>
internal sealed class MedianFunction : SqlAggregateFunction
{
    public MedianFunction()
        : base("median", [SqlType.Double], SqlType.Double)
    {
    }

    protected override SqlAggregateAccumulator CreateAccumulatorCore(scoped in SqlFunctionContext context) => new Accumulator();

    private sealed class Accumulator : SqlAggregateAccumulator
    {
        private readonly List<double> _values = [];

        protected override void AddCore(scoped in SqlArguments arguments) => _values.Add(arguments.GetDouble(0));

        protected override SqlValue FinishCore()
        {
            if (_values.Count == 0)
            {
                return SqlValue.Null;
            }

            _values.Sort();
            int middle = _values.Count / 2;
            return SqlValue.FromDouble(_values.Count % 2 == 1 ? _values[middle] : (_values[middle - 1] + _values[middle]) / 2);
        }
    }
}

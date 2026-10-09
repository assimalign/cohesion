using System;

using Assimalign.Cohesion.Database.Types;

namespace Assimalign.Cohesion.Database.Sql.Internal;

/// <summary>
/// <c>SUM(numeric)</c> and <c>AVG(numeric)</c>: the sum, or the mean, of a group's non-NULL values,
/// accumulated in DECIMAL with checked arithmetic. An overflow fails the statement as
/// <c>COHSQLE002</c>; a group with no non-NULL value returns NULL. The parameter is the numeric
/// pseudo-type, so a call over text fails while planning (<c>COHSQLE006</c>); a non-number that only
/// a row reveals fails when it is added.
/// </summary>
internal sealed class SqlSumFunction : SqlAggregateFunction
{
    private readonly bool _average;

    /// <summary>Initializes one of the two functions.</summary>
    /// <param name="average"><see langword="true"/> for <c>AVG</c>; <see langword="false"/> for <c>SUM</c>.</param>
    internal SqlSumFunction(bool average)
        : base(average ? "AVG" : "SUM", [SqlType.AnyNumeric], SqlType.Numeric)
    {
        _average = average;
        Usage = average ? "AVG(numeric)" : "SUM(numeric)";
    }

    /// <inheritdoc />
    protected override SqlAggregateAccumulator CreateAccumulatorCore(scoped in SqlFunctionContext context)
        => new Accumulator(Name, _average);

    /// <summary>One group's count and decimal sum.</summary>
    private sealed class Accumulator : SqlAggregateAccumulator
    {
        private readonly string _name;
        private readonly bool _average;
        private long _count;
        private decimal _sum;

        internal Accumulator(string name, bool average)
        {
            _name = name;
            _average = average;
        }

        /// <inheritdoc />
        /// <exception cref="SqlEvaluationException">The sum or the count overflowed (<c>COHSQLE002</c>).</exception>
        /// <exception cref="DatabaseException">The value is not a number.</exception>
        protected override void AddCore(scoped in SqlArguments arguments)
        {
            var value = arguments[0];
            try
            {
                _count = checked(_count + 1);
                decimal number = value.Type switch
                {
                    DatabaseType.Int8 => value.AsSByte(),
                    DatabaseType.Int16 => value.AsInt16(),
                    DatabaseType.Int32 => value.AsInt32(),
                    DatabaseType.Int64 => value.AsInt64(),
                    DatabaseType.Float32 => Convert.ToDecimal(value.AsSingle()),
                    DatabaseType.Float64 => Convert.ToDecimal(value.AsDouble()),
                    DatabaseType.Decimal => value.AsDecimal(),
                    _ => throw new DatabaseException($"{_name} requires a numeric argument."),
                };
                _sum = checked(_sum + number);
            }
            catch (OverflowException exception)
            {
                throw SqlEvaluationException.NumericValueOutOfRange(
                    $"{_name} overflowed; the aggregate cannot be represented as a decimal or count.", exception);
            }
        }

        /// <summary>Decimal division keeps a fractional mean, rounding to even at the last digit.</summary>
        /// <returns>The sum or the mean; NULL for a group without a non-NULL value.</returns>
        protected override SqlValue FinishCore()
            => _count == 0 ? SqlValue.Null : SqlValue.FromDecimal(_average ? _sum / _count : _sum);
    }
}

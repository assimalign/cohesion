using System;

namespace Assimalign.Cohesion.Database.Sql.Internal;

/// <summary>
/// <c>COUNT</c>, two overloads: <c>COUNT(*)</c>, which has no parameters and counts every row, and
/// <c>COUNT(value)</c>, strict over <see cref="SqlType.Any"/>, which counts the rows whose value is
/// not NULL. Both return a BIGINT that is never NULL: an empty group counts 0.
/// </summary>
internal sealed class SqlCountFunction : SqlAggregateFunction
{
    /// <summary>Initializes an overload.</summary>
    /// <param name="rows"><see langword="true"/> for <c>COUNT(*)</c>; <see langword="false"/> for <c>COUNT(value)</c>.</param>
    internal SqlCountFunction(bool rows)
        : base("COUNT", rows ? [] : [SqlType.Any], SqlType.BigInt)
    {
        Usage = rows ? "COUNT(*)" : "COUNT(value)";
        IsNeverNull = true;
    }

    /// <inheritdoc />
    protected override SqlAggregateAccumulator CreateAccumulatorCore(scoped in SqlFunctionContext context) => new Accumulator();

    /// <summary>One group's count.</summary>
    private sealed class Accumulator : SqlAggregateAccumulator
    {
        private long _count;

        /// <inheritdoc />
        /// <exception cref="SqlEvaluationException">The count passed BIGINT (<c>COHSQLE002</c>).</exception>
        protected override void AddCore(scoped in SqlArguments arguments)
        {
            try
            {
                _count = checked(_count + 1);
            }
            catch (OverflowException exception)
            {
                throw SqlEvaluationException.NumericValueOutOfRange(
                    "COUNT overflowed; the aggregate cannot be represented as a decimal or count.", exception);
            }
        }

        /// <inheritdoc />
        protected override SqlValue FinishCore() => SqlValue.FromInt64(_count);
    }
}

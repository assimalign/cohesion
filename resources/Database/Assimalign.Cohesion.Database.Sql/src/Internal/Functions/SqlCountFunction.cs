using System;

namespace Assimalign.Cohesion.Database.Sql.Internal;

/// <summary>
/// <c>COUNT</c>, two overloads: <c>COUNT(*)</c>, which has no parameters and counts every row, and
/// <c>COUNT(value)</c>, strict over <see cref="SqlType.Any"/>, which counts the rows whose value is
/// not NULL. Both return a BIGINT that is never NULL: an empty group counts 0.
/// </summary>
[global::System.Diagnostics.DebuggerDisplay("SqlCountFunction {Name,nq}")]
[global::System.Diagnostics.DebuggerTypeProxy(typeof(global::Assimalign.Cohesion.Database.Sql.Internal.SqlCountFunction.DebugView))]
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
    [global::System.Diagnostics.DebuggerDisplay("Accumulator")]
    [global::System.Diagnostics.DebuggerTypeProxy(typeof(global::Assimalign.Cohesion.Database.Sql.Internal.SqlCountFunction.Accumulator.DebugView))]
    private sealed class Accumulator : SqlAggregateAccumulator
    {
        private long _count;

        /// <inheritdoc />
        /// <exception cref="SqlEvaluationException">The count passed BIGINT (<c>CS02002</c>).</exception>
        protected override void AddCore(scoped in SqlArguments arguments)
        {
            try
            {
                _count = checked(_count + 1);
            }
            catch (OverflowException exception)
            {
                throw SqlEvaluationException.NumericValueOutOfRange(
                    ErrorMessages.AccumulatorAddCoreSqlEvaluation, exception);
            }
        }

        /// <inheritdoc />
        protected override SqlValue FinishCore() => SqlValue.FromInt64(_count);

        private sealed class DebugView
        {
            public DebugView(global::Assimalign.Cohesion.Database.Sql.Internal.SqlCountFunction.Accumulator value)
            {
                _ = value;
            }
        }
    }

    private sealed class DebugView
    {
        private readonly global::Assimalign.Cohesion.Database.Sql.Internal.SqlCountFunction _target;

        public DebugView(global::Assimalign.Cohesion.Database.Sql.Internal.SqlCountFunction value)
        {
            _target = value;
        }

        public string Name => _target.Name;
        public global::Assimalign.Cohesion.Database.Sql.SqlFunctionKind Kind => _target.Kind;
        public global::Assimalign.Cohesion.Database.Sql.SqlType? VariadicParameter => _target.VariadicParameter;
        public global::Assimalign.Cohesion.Database.Sql.SqlType ReturnType => _target.ReturnType;
        public global::Assimalign.Cohesion.Database.Sql.SqlFunctionVolatility Volatility => _target.Volatility;
        public global::Assimalign.Cohesion.Database.Sql.SqlNullBehavior NullBehavior => _target.NullBehavior;
        public bool IsNeverNull => _target.IsNeverNull;
    }
}

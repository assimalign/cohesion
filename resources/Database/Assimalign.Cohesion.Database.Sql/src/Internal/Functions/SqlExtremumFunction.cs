using System;

using Assimalign.Cohesion.Database.Types;

namespace Assimalign.Cohesion.Database.Sql.Internal;

/// <summary>
/// <c>MIN(value)</c> and <c>MAX(value)</c>: a group's least or greatest non-NULL value, in the
/// order grouping and sorting use (<see cref="SqlValueComparer"/>), text under the collation the
/// call's context carries. The parameter and result are <see cref="SqlType.AnyElement"/>; a group
/// with no non-NULL value returns NULL.
/// </summary>
[global::System.Diagnostics.DebuggerDisplay("SqlExtremumFunction {Name,nq}")]
[global::System.Diagnostics.DebuggerTypeProxy(typeof(global::Assimalign.Cohesion.Database.Sql.Internal.SqlExtremumFunction.DebugView))]
internal sealed class SqlExtremumFunction : SqlAggregateFunction
{
    private readonly bool _maximum;

    /// <summary>Initializes one of the two functions.</summary>
    /// <param name="maximum"><see langword="true"/> for <c>MAX</c>; <see langword="false"/> for <c>MIN</c>.</param>
    internal SqlExtremumFunction(bool maximum)
        : base(maximum ? "MAX" : "MIN", [SqlType.AnyElement], SqlType.AnyElement)
    {
        _maximum = maximum;
        Usage = maximum ? "MAX(value)" : "MIN(value)";
    }

    /// <inheritdoc />
    protected override SqlAggregateAccumulator CreateAccumulatorCore(scoped in SqlFunctionContext context)
        => new Accumulator(Name, _maximum ? 1 : -1, context.Collation);

    /// <summary>One group's extreme value.</summary>
    [global::System.Diagnostics.DebuggerDisplay("Accumulator")]
    [global::System.Diagnostics.DebuggerTypeProxy(typeof(global::Assimalign.Cohesion.Database.Sql.Internal.SqlExtremumFunction.Accumulator.DebugView))]
    private sealed class Accumulator : SqlAggregateAccumulator
    {
        private readonly string _name;
        private readonly int _direction;
        private readonly Collation _collation;
        private long _count;
        private SqlValue _extreme;

        internal Accumulator(string name, int direction, Collation collation)
        {
            _name = name;
            _direction = direction;
            _collation = collation;
        }

        /// <inheritdoc />
        /// <exception cref="SqlEvaluationException">The row count overflowed (<c>CS02002</c>).</exception>
        protected override void AddCore(scoped in SqlArguments arguments)
        {
            var value = arguments[0];
            try
            {
                _count = checked(_count + 1);
            }
            catch (OverflowException exception)
            {
                throw SqlEvaluationException.NumericValueOutOfRange(
                    string.Format(System.Globalization.CultureInfo.CurrentCulture, ErrorMessages.AccumulatorAddCoreSqlEvaluation2, _name), exception);
            }

            // A value the engine read from a row keeps the box it was read from, so comparing and
            // keeping it allocates nothing.
            if (_extreme.IsNull || Math.Sign(SqlValue.Compare(value, _extreme, _collation)) == _direction)
            {
                _extreme = value;
            }
        }

        /// <inheritdoc />
        protected override SqlValue FinishCore() => _extreme;

        private sealed class DebugView
        {
            public DebugView(global::Assimalign.Cohesion.Database.Sql.Internal.SqlExtremumFunction.Accumulator value)
            {
                _ = value;
            }
        }
    }

    private sealed class DebugView
    {
        private readonly global::Assimalign.Cohesion.Database.Sql.Internal.SqlExtremumFunction _target;

        public DebugView(global::Assimalign.Cohesion.Database.Sql.Internal.SqlExtremumFunction value)
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

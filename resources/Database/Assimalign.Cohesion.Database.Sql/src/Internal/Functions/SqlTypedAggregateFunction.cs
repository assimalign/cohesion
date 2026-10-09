using System;

namespace Assimalign.Cohesion.Database.Sql.Internal;

/// <summary>
/// The aggregate <see cref="SqlAggregateFunction.Create{TState, T1, TResult}"/> builds: strict, its
/// state seeded per group, folded per non-NULL argument and finished once, a group without a
/// non-NULL argument returning NULL without calling the finish delegate.
/// </summary>
/// <typeparam name="TState">The state's CLR type.</typeparam>
/// <typeparam name="T1">The argument's CLR type.</typeparam>
/// <typeparam name="TResult">The result's CLR type.</typeparam>
internal sealed class SqlTypedAggregateFunction<TState, T1, TResult> : SqlAggregateFunction
{
    private readonly Func<TState> _seed;
    private readonly Func<TState, T1, TState> _step;
    private readonly Func<TState, TResult> _finish;

    /// <summary>Initializes the aggregate; the type arguments map to SQL types here.</summary>
    /// <param name="name">The name calls use.</param>
    /// <param name="seed">Creates a group's initial state.</param>
    /// <param name="step">Combines the state with one argument.</param>
    /// <param name="finish">Computes the result from the state.</param>
    /// <exception cref="ArgumentNullException">A delegate is null.</exception>
    /// <exception cref="NotSupportedException"><typeparamref name="T1"/> or <typeparamref name="TResult"/> has no SQL type.</exception>
    internal SqlTypedAggregateFunction(string name, Func<TState> seed, Func<TState, T1, TState> step, Func<TState, TResult> finish)
        : base(name, [SqlValueConverter<T1>.Type], SqlValueConverter<TResult>.Type)
    {
        ArgumentNullException.ThrowIfNull(seed);
        ArgumentNullException.ThrowIfNull(step);
        ArgumentNullException.ThrowIfNull(finish);
        _seed = seed;
        _step = step;
        _finish = finish;
    }

    /// <inheritdoc />
    protected override SqlAggregateAccumulator CreateAccumulatorCore(scoped in SqlFunctionContext context)
        => new Accumulator(this, _seed());

    /// <summary>One group's state.</summary>
    private sealed class Accumulator : SqlAggregateAccumulator
    {
        private readonly SqlTypedAggregateFunction<TState, T1, TResult> _function;
        private TState _state;
        private bool _seen;

        internal Accumulator(SqlTypedAggregateFunction<TState, T1, TResult> function, TState state)
        {
            _function = function;
            _state = state;
        }

        /// <inheritdoc />
        protected override void AddCore(scoped in SqlArguments arguments)
        {
            _state = _function._step(_state, SqlValueConverter<T1>.FromValue(arguments[0]));
            _seen = true;
        }

        /// <inheritdoc />
        protected override SqlValue FinishCore()
            => _seen ? SqlValueConverter<TResult>.ToValue(_function._finish(_state)) : SqlValue.Null;
    }
}

using System;

using Assimalign.Cohesion.Database.Sql.Internal;

namespace Assimalign.Cohesion.Database.Sql;

/// <summary>
/// A function that computes one value from the rows of a group: the engine's <c>COUNT</c>,
/// <c>SUM</c>, <c>AVG</c>, <c>MIN</c> and <c>MAX</c>, and an application's own.
/// </summary>
/// <remarks>
/// <para>
/// <b>An inverted seam:</b> the engine drives it and the standard library and applications
/// implement it, by deriving and overriding <see cref="CreateAccumulatorCore"/>, or through the
/// typed shorthand <see cref="Create{TState, T1, TResult}"/>. The function itself is stateless; the
/// engine creates one <see cref="SqlAggregateAccumulator"/> per group, per aggregate call, per
/// statement, adds the group's rows to it, and finishes it once, an empty group included.
/// </para>
/// <para>
/// <b>Calls.</b> A function without parameters is called as <c>name(*)</c>, as <c>COUNT(*)</c> is; a
/// call with no arguments at all, <c>name()</c>, does not resolve to it (PostgreSQL's rule for a
/// parameterless aggregate). A strict aggregate (the default) skips every row with a NULL argument.
/// An aggregate is never folded and never admitted in a CHECK, so its
/// <see cref="SqlFunction.Volatility"/> is <see cref="SqlFunctionVolatility.Volatile"/>.
/// </para>
/// </remarks>
// Deviates from the repo interface-first rule per design decision: Database engines are concrete-first — abstract bases with protected cores and sealed model leaves (owner, 2026-10-04; database-area.md).
public abstract class SqlAggregateFunction : SqlFunction
{
    /// <summary>Initializes an aggregate function.</summary>
    /// <param name="name">The name calls use, matched ignoring case.</param>
    /// <param name="parameters">The parameter types, in order; empty for an aggregate called as <c>name(*)</c>.</param>
    /// <param name="returnType">The result type.</param>
    /// <param name="nullBehavior">What a row with a NULL argument does; skipped by default.</param>
    /// <exception cref="ArgumentNullException"><paramref name="name"/>, <paramref name="returnType"/> or a parameter type is null.</exception>
    /// <exception cref="ArgumentException">The name is blank, or the result type is a pseudo-type the parameters cannot determine.</exception>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="nullBehavior"/> is not defined.</exception>
    protected SqlAggregateFunction(string name, ReadOnlySpan<SqlType> parameters, SqlType returnType,
        SqlNullBehavior nullBehavior = SqlNullBehavior.ReturnsNullOnNullInput)
        : base(name, SqlFunctionKind.Aggregate, parameters, returnType, SqlFunctionVolatility.Volatile, nullBehavior, null)
    {
    }

    /// <summary>Creates the accumulator of one group.</summary>
    /// <param name="context">The context of the statement and the aggregate call.</param>
    /// <returns>A new accumulator, owned by the caller and used by one thread at a time.</returns>
    /// <exception cref="DatabaseException">
    /// The function failed (<c>COHSQLE007</c>): it threw, returned no accumulator, or returned one
    /// that was already in use.
    /// </exception>
    public SqlAggregateAccumulator CreateAccumulator(scoped in SqlFunctionContext context)
    {
        SqlAggregateAccumulator? accumulator;
        try
        {
            accumulator = CreateAccumulatorCore(in context);
        }
        catch (Exception exception) when (SqlEvaluationException.IsFunctionFailure(exception))
        {
            throw SqlEvaluationException.FunctionFailed(Name, exception);
        }

        if (accumulator is null)
        {
            throw SqlEvaluationException.FunctionFailed(Name,
                new InvalidOperationException($"Aggregate '{Name}' returned no accumulator."));
        }

        if (!accumulator.TryAttach(this))
        {
            throw SqlEvaluationException.FunctionFailed(Name,
                new InvalidOperationException($"Aggregate '{Name}' returned an accumulator that is already in use; create a new one for every group."));
        }

        return accumulator;
    }

    /// <summary>Creates a new accumulator; the engine calls it through <see cref="CreateAccumulator"/>, once per group.</summary>
    /// <param name="context">The context of the statement and the aggregate call.</param>
    /// <returns>A new accumulator, never one returned before.</returns>
    protected abstract SqlAggregateAccumulator CreateAccumulatorCore(scoped in SqlFunctionContext context);

    /// <summary>
    /// Creates an aggregate of one argument from three delegates: <paramref name="seed"/> starts a
    /// group's state, <paramref name="step"/> folds each row's argument into it, and
    /// <paramref name="finish"/> turns it into the result. By default the aggregate is strict: a row
    /// whose argument is NULL is skipped, and a group that saw no non-NULL argument returns NULL
    /// without calling <paramref name="finish"/>. The CLR types map to SQL types when this method
    /// runs, as for <see cref="SqlScalarFunction.Create{T1, TResult}"/>; the state is any CLR type
    /// and never leaves the accumulator.
    /// </summary>
    /// <typeparam name="TState">The state's CLR type.</typeparam>
    /// <typeparam name="T1">The argument's CLR type.</typeparam>
    /// <typeparam name="TResult">The result's CLR type.</typeparam>
    /// <param name="name">The name calls use.</param>
    /// <param name="seed">Creates a group's initial state.</param>
    /// <param name="step">Combines the state with one argument.</param>
    /// <param name="finish">Computes the result from the state.</param>
    /// <param name="nullBehavior">
    /// What a row with a NULL argument does: skipped by default; with
    /// <see cref="SqlNullBehavior.CalledOnNullInput"/> <paramref name="step"/> receives it as
    /// <see langword="null"/>, so <typeparamref name="T1"/> must be a reference or nullable type, and
    /// only a group without rows returns NULL without calling <paramref name="finish"/>.
    /// </param>
    /// <returns>The function, to register with <see cref="SqlFunctionCollection.Add"/>.</returns>
    /// <exception cref="ArgumentNullException">An argument is null.</exception>
    /// <exception cref="ArgumentException">
    /// <paramref name="name"/> is blank, or the aggregate is called on NULL input and <typeparamref name="T1"/> cannot hold NULL.
    /// </exception>
    /// <exception cref="NotSupportedException"><typeparamref name="T1"/> or <typeparamref name="TResult"/> has no SQL type.</exception>
    public static SqlAggregateFunction Create<TState, T1, TResult>(string name, Func<TState> seed,
        Func<TState, T1, TState> step, Func<TState, TResult> finish,
        SqlNullBehavior nullBehavior = SqlNullBehavior.ReturnsNullOnNullInput)
        => new SqlTypedAggregateFunction<TState, T1, TResult>(name, seed, step, finish, nullBehavior);
}

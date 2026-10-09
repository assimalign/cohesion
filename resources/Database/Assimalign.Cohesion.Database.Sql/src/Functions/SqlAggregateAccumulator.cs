using System;

using Assimalign.Cohesion.Database.Sql.Internal;

namespace Assimalign.Cohesion.Database.Sql;

/// <summary>
/// The state of one aggregate over one group: the engine adds each of the group's rows, then
/// finishes it once.
/// </summary>
/// <remarks>
/// <para>
/// <b>Ownership.</b> One accumulator per group, per aggregate call, per statement, created by
/// <see cref="SqlAggregateFunction.CreateAccumulator"/> and owned by the engine, which uses it from
/// one thread at a time and never shares it. It may therefore keep plain mutable state.
/// </para>
/// <para>
/// <b>NVI.</b> <see cref="Add"/> skips a row with a NULL argument when its function is strict, so
/// <see cref="AddCore"/> never sees one; <see cref="Finish"/> runs <see cref="FinishCore"/> exactly
/// once, for an empty group too. Anything a core throws other than
/// <see cref="OperationCanceledException"/>, <see cref="InsufficientExecutionStackException"/>,
/// <see cref="OutOfMemoryException"/> or a <see cref="DatabaseException"/> fails the statement as
/// <c>COHSQLE007</c>, naming the function.
/// </para>
/// </remarks>
// Deviates from the repo interface-first rule per design decision: Database engines are concrete-first — abstract bases with protected cores and sealed model leaves (owner, 2026-10-04; database-area.md).
public abstract class SqlAggregateAccumulator
{
    private SqlAggregateFunction? _function;
    private bool _finished;

    /// <summary>Initializes an accumulator; its function attaches when the engine receives it.</summary>
    protected SqlAggregateAccumulator()
    {
    }

    /// <summary>Adds one row of the group.</summary>
    /// <param name="arguments">The row's arguments, already of the declared types.</param>
    /// <exception cref="InvalidOperationException">
    /// The accumulator was not created by <see cref="SqlAggregateFunction.CreateAccumulator"/>, or it is finished.
    /// </exception>
    /// <exception cref="ArgumentException">The number of arguments does not match the function's parameters.</exception>
    /// <exception cref="DatabaseException">The function failed: <c>COHSQLE007</c>, or the database exception it threw.</exception>
    public void Add(scoped in SqlArguments arguments)
    {
        var function = _function
            ?? throw new InvalidOperationException("The accumulator was not created by SqlAggregateFunction.CreateAccumulator.");
        if (_finished)
        {
            throw new InvalidOperationException($"The accumulator of aggregate '{function.Name}' is finished.");
        }
        if (arguments.Count != function.ParameterTypes.Length)
        {
            throw new ArgumentException(
                $"Aggregate '{function.Name}' takes {function.ParameterTypes.Length} argument(s), but {arguments.Count} were passed.",
                nameof(arguments));
        }
        AddResolved(in arguments);
    }

    /// <summary>
    /// The engine's addition of a row to an accumulator it created and has not finished, with as
    /// many arguments as the function declares: the strict skip of a row with a NULL argument and
    /// the one <see cref="AddCore"/> call, its failure coded, as <see cref="Add"/> does.
    /// </summary>
    /// <param name="arguments">The row's arguments, of their parameters' types.</param>
    /// <exception cref="DatabaseException">The function failed (<c>COHSQLE007</c>), or the database exception it threw.</exception>
    internal void AddResolved(scoped in SqlArguments arguments)
    {
        if (_function!.NullBehavior == SqlNullBehavior.ReturnsNullOnNullInput && arguments.HasNull)
        {
            return;
        }

        AddCoded(in arguments);
    }

    /// <summary>
    /// Adds a row the strict rule admits: the one <see cref="AddCore"/> call, its failure coded. The
    /// engine calls it directly for a one-argument aggregate, having skipped a NULL row itself.
    /// </summary>
    /// <param name="arguments">The row's arguments, of their parameters' types.</param>
    /// <exception cref="DatabaseException">The function failed (<c>COHSQLE007</c>), or the database exception it threw.</exception>
    internal void AddCoded(scoped in SqlArguments arguments)
    {
        try
        {
            AddCore(in arguments);
        }
        catch (Exception exception) when (SqlEvaluationException.IsFunctionFailure(exception))
        {
            throw SqlEvaluationException.FunctionFailed(_function!.Name, exception);
        }
    }

    /// <summary>Computes the group's result; called once per group, after its last row.</summary>
    /// <returns>The result.</returns>
    /// <exception cref="InvalidOperationException">
    /// The accumulator was not created by <see cref="SqlAggregateFunction.CreateAccumulator"/>, or it is already finished.
    /// </exception>
    /// <exception cref="DatabaseException">The function failed: <c>COHSQLE007</c>, or the database exception it threw.</exception>
    public SqlValue Finish()
    {
        var function = _function
            ?? throw new InvalidOperationException("The accumulator was not created by SqlAggregateFunction.CreateAccumulator.");
        if (_finished)
        {
            throw new InvalidOperationException($"The accumulator of aggregate '{function.Name}' is already finished.");
        }

        _finished = true;
        try
        {
            return FinishCore();
        }
        catch (Exception exception) when (SqlEvaluationException.IsFunctionFailure(exception))
        {
            throw SqlEvaluationException.FunctionFailed(function.Name, exception);
        }
    }

    /// <summary>Adds one row; the engine calls it through <see cref="Add"/>.</summary>
    /// <param name="arguments">The row's arguments: of the declared types, and none NULL when the function is strict.</param>
    protected abstract void AddCore(scoped in SqlArguments arguments);

    /// <summary>Computes the result; the engine calls it through <see cref="Finish"/>, once.</summary>
    /// <returns>The result, of the function's declared type, or <see cref="SqlValue.Null"/>.</returns>
    protected abstract SqlValue FinishCore();

    /// <summary>Attaches the function that created the accumulator; refused once one is attached.</summary>
    /// <param name="function">The function.</param>
    /// <returns><see langword="false"/> when the accumulator already belongs to a group.</returns>
    internal bool TryAttach(SqlAggregateFunction function)
    {
        if (_function is not null)
        {
            return false;
        }

        _function = function;
        return true;
    }
}

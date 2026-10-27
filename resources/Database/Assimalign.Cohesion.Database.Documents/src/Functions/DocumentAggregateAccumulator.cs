using System;
using System.Threading;

using Assimalign.Cohesion.Database.Documents.Internal;

namespace Assimalign.Cohesion.Database.Documents;

/// <summary>One aggregate call's state for one group; never shared or reused.</summary>
// Deviates from the repo interface-first rule per design decision: Database engines are concrete-first — abstract bases with protected cores and sealed model leaves (owner, 2026-10-04; database-area.md).
public abstract class DocumentAggregateAccumulator
{
    private DocumentAggregateFunction? _function;
    private CancellationToken _cancellationToken;
    private bool _finished;

    /// <summary>Initializes a fresh accumulator.</summary>
    protected DocumentAggregateAccumulator() { }
    internal bool TryAttach(DocumentAggregateFunction function, CancellationToken cancellationToken)
    {
        // The single ownership claim is per accumulator, never a process-wide counter.
        if (Interlocked.CompareExchange(ref _function, function, null) is not null) { return false; }
        _cancellationToken = cancellationToken;
        return true;
    }
    /// <summary>Adds a row admitted by the aggregate's NULL/MISSING rule.</summary><param name="arguments">The row arguments.</param>
    /// <exception cref="InvalidOperationException">The accumulator is unattached or finished.</exception><exception cref="ArgumentException">The argument count is invalid.</exception><exception cref="DatabaseException">The aggregate core failed.</exception><exception cref="OperationCanceledException">The statement was canceled.</exception>
    public void Add(scoped in DocumentArguments arguments)
    {
        var function = _function ?? throw new InvalidOperationException(ErrorMessages.FunctionAccumulatorInvalid);
        if (_finished) { throw new InvalidOperationException(ErrorMessages.FunctionAccumulatorInvalid); }
        _cancellationToken.ThrowIfCancellationRequested();
        function.CheckArity(arguments.Count);
        if (function.NullBehavior == DocumentNullBehavior.PropagateMissingOrNull && function.Propagate(in arguments, out _)) { return; }
        for (int index = 0; index < arguments.Count; index++)
        {
            if (!arguments[index].IsNull && !arguments[index].IsMissing &&
                !function.AcceptsArgument(index, DocumentExpressionEvaluator.FunctionKind(arguments[index].Value))) { return; }
        }
        try { AddCore(in arguments); }
        catch (Exception error) when (DocumentPlanningException.IsExternalFunctionFailure(error, _cancellationToken))
        { throw DocumentPlanningException.FunctionFailed(function.Name, error); }
    }
    /// <summary>Finishes the group once, including an empty group.</summary><returns>The checked result.</returns>
    /// <exception cref="InvalidOperationException">The accumulator is unattached or finished.</exception><exception cref="DatabaseException">The function failed or returned an incompatible result.</exception><exception cref="OperationCanceledException">The statement was canceled.</exception>
    public DocumentValue Finish()
    {
        var function = _function ?? throw new InvalidOperationException(ErrorMessages.FunctionAccumulatorInvalid);
        if (_finished) { throw new InvalidOperationException(ErrorMessages.FunctionAccumulatorInvalid); }
        _finished = true;
        _cancellationToken.ThrowIfCancellationRequested();
        try { return function.CheckResult(FinishCore()); }
        catch (Exception error) when (DocumentPlanningException.IsExternalFunctionFailure(error, _cancellationToken))
        { throw DocumentPlanningException.FunctionFailed(function.Name, error); }
    }
    /// <summary>Combines one admitted row.</summary><param name="arguments">The borrowed row arguments.</param>
    protected abstract void AddCore(scoped in DocumentArguments arguments);
    /// <summary>Computes the final value.</summary><returns>The aggregate result.</returns>
    protected abstract DocumentValue FinishCore();
}

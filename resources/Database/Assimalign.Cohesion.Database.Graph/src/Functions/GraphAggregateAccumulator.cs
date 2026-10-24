using System;
using System.Threading;
using Assimalign.Cohesion.Database.Graph.Internal;

namespace Assimalign.Cohesion.Database.Graph;

/// <summary>Single-group aggregate state with checked callbacks and statement-owned memory accounting.</summary>
/// <remarks>Developers must report all retained private state through <see cref="RetainedBytesCore"/>. Private callback CPU and temporary allocations remain trusted.</remarks>
// Deviates from the repo interface-first rule per design decision: Database engines are concrete-first — abstract bases with protected cores and sealed model leaves (owner, 2026-10-04; database-area.md).
public abstract class GraphAggregateAccumulator : IDisposable
{
    private GraphAggregateFunction? _function;
    private CancellationToken _token;
    private GraphMemoryOwner? _memory;
    private long _charged;
    private bool _disposed;
    private bool _finished;
    private int _attached;
    /// <summary>Initializes fresh state; the engine attaches it to one group only.</summary>
    protected GraphAggregateAccumulator() { }
    internal void Attach(GraphAggregateFunction function, CancellationToken token)
    {
        if (Interlocked.CompareExchange(ref _attached, 1, 0) != 0) { throw GraphPlanningException.FunctionContract(function.Name); }
        _function = function; _token = token;
    }
    internal void Own(GraphMemoryOwner? memory)
    {
        _memory = memory;
        try { Charge(); }
        catch (Exception error) when (GraphPlanningException.IsExternalFunctionFailure(error, _token))
        { throw GraphPlanningException.FunctionFailed(_function!.Name, error); }
    }
    private void Charge()
    {
        long retained = RetainedBytesCore;
        if (retained < 0) { throw GraphPlanningException.FunctionContract(_function!.Name); }
        if (retained > _charged) { _memory?.Add(retained - _charged); }
        else if (retained < _charged) { _memory?.Release(_charged - retained); }
        _charged = retained;
    }
    /// <summary>Advances the group using checked borrowed arguments.</summary><param name="arguments">The row inputs.</param>
    /// <exception cref="ObjectDisposedException">The state is disposed.</exception>
    /// <exception cref="InvalidOperationException">The state is unattached or already finished.</exception>
    /// <exception cref="DatabaseException">A callback/contract fails, or the statement budget is exceeded.</exception>
    /// <exception cref="OperationCanceledException">Statement cancellation was requested.</exception>
    public void Add(scoped in GraphArguments arguments)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_finished) { throw new InvalidOperationException(ErrorMessages.FunctionAccumulatorFinished); }
        var function = _function ?? throw new InvalidOperationException(ErrorMessages.FunctionAccumulatorUnattached);
        _token.ThrowIfCancellationRequested();
        if (!function.AcceptsArity(arguments.Count)) { throw GraphPlanningException.FunctionContract(function.Name); }
        if (function.NullBehavior == GraphNullBehavior.Strict)
        { for (int i = 0; i < arguments.Count; i++) { if (arguments[i] is null) { return; } } }
        if (!function.IsNative)
        { for (int i = 0; i < arguments.Count; i++) { GraphPlanningException.ValidateFunctionValue(function, arguments[i], function.Parameters[i]); } }
        try { AddCore(in arguments); Charge(); _token.ThrowIfCancellationRequested(); }
        catch (Exception error) when (GraphPlanningException.IsExternalFunctionFailure(error, _token)) { throw GraphPlanningException.FunctionFailed(function.Name, error); }
    }
    /// <summary>Finishes the group and checks its result contract.</summary><returns>The query value.</returns>
    /// <exception cref="ObjectDisposedException">The state is disposed.</exception>
    /// <exception cref="InvalidOperationException">The state is unattached or already finished.</exception>
    /// <exception cref="DatabaseException">A callback/contract fails, or the statement budget is exceeded.</exception>
    /// <exception cref="OperationCanceledException">Statement cancellation was requested.</exception>
    public object? Finish()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_finished) { throw new InvalidOperationException(ErrorMessages.FunctionAccumulatorFinished); }
        var function = _function ?? throw new InvalidOperationException(ErrorMessages.FunctionAccumulatorUnattached);
        _token.ThrowIfCancellationRequested();
        _finished = true;
        try
        {
            object? result = FinishCore(); Charge(); _token.ThrowIfCancellationRequested();
            if (!function.IsNative) { GraphPlanningException.ValidateFunctionValue(function, result, function.ReturnType); }
            return result;
        }
        catch (Exception error) when (GraphPlanningException.IsExternalFunctionFailure(error, _token)) { throw GraphPlanningException.FunctionFailed(function.Name, error); }
    }
    /// <summary>Advances already checked state.</summary><param name="arguments">The borrowed inputs.</param>
    protected abstract void AddCore(scoped in GraphArguments arguments);
    /// <summary>Produces the result, including for an empty group.</summary><returns>The query value.</returns>
    protected abstract object? FinishCore();
    /// <summary>Gets the nonnegative number of bytes retained by private state. Include referenced values; do not include the framework's fixed group charge.</summary>
    protected abstract long RetainedBytesCore { get; }
    /// <summary>Releases private resources. No I/O is required by built-in accumulators.</summary>
    protected virtual void DisposeCore() { }
    /// <summary>Releases this group's memory charge and private resources once.</summary>
    /// <exception cref="DatabaseException">A developer disposal callback fails (CG05021).</exception>
    public void Dispose()
    {
        if (_disposed) { return; } _disposed = true;
        try { DisposeCore(); }
        catch (Exception error) when (_function is not null && GraphPlanningException.IsExternalFunctionFailure(error, _token))
        { throw GraphPlanningException.FunctionFailed(_function.Name, error); }
        finally { _memory?.Release(_charged); _charged = 0; _memory = null; }
    }
}

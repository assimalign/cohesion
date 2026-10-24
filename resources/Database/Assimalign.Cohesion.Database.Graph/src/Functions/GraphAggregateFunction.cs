using System;
using System.Collections.Generic;
using Assimalign.Cohesion.Database.Graph.Internal;
using Assimalign.Cohesion.Database.Graph.Language;

namespace Assimalign.Cohesion.Database.Graph;

/// <summary>A per-group aggregate factory sharing the native and developer invocation contract.</summary>
// Deviates from the repo interface-first rule per design decision: Database engines are concrete-first — abstract bases with protected cores and sealed model leaves (owner, 2026-10-04; database-area.md).
public abstract class GraphAggregateFunction : GraphFunction
{
    /// <summary>Initializes immutable aggregate metadata.</summary><param name="name">The name.</param>
    /// <param name="parameters">The parameter shapes.</param><param name="returnType">The result shape.</param><param name="nullBehavior">The NULL contract.</param>
    /// <exception cref="ArgumentException">The name is default/invalid or a parameter shape is null.</exception>
    /// <exception cref="ArgumentNullException">Parameter metadata or return metadata is null.</exception>
    /// <exception cref="ArgumentOutOfRangeException">The NULL behavior is unknown.</exception>
    protected GraphAggregateFunction(GraphFunctionName name, IEnumerable<GraphFunctionType> parameters, GraphFunctionType returnType,
        GraphNullBehavior nullBehavior = GraphNullBehavior.Strict) : base(name, parameters, returnType, nullBehavior) { }
    /// <summary>Creates fresh state for exactly one group.</summary><param name="context">The borrowed statement context.</param><returns>New state.</returns>
    /// <exception cref="DatabaseException">The callback fails, returns null or reuses attached state (CG05021).</exception>
    /// <exception cref="OperationCanceledException">Statement cancellation was requested.</exception>
    public GraphAggregateAccumulator CreateAccumulator(scoped in GraphFunctionContext context)
    {
        context.CancellationToken.ThrowIfCancellationRequested();
        try
        {
            var result = CreateAccumulatorCore(in context);
            if (result is null) { throw GraphPlanningException.FunctionContract(Name); }
            result.Attach(this, context.CancellationToken);
            return result;
        }
        catch (Exception error) when (GraphPlanningException.IsExternalFunctionFailure(error, context.CancellationToken))
        { throw GraphPlanningException.FunctionFailed(Name, error); }
    }
    /// <summary>Creates unused state; returning a previously attached state is refused.</summary>
    /// <param name="context">The borrowed context.</param><returns>The new state.</returns>
    protected abstract GraphAggregateAccumulator CreateAccumulatorCore(scoped in GraphFunctionContext context);
    /// <summary>Creates a typed single-input aggregate with an explicit retained-memory contract.</summary>
    /// <typeparam name="TState">The private state type.</typeparam><typeparam name="T1">The input type.</typeparam><typeparam name="TResult">The result type.</typeparam>
    /// <param name="name">The name.</param><param name="seed">Creates independent state.</param><param name="step">Advances it.</param>
    /// <param name="finish">Produces the result, including empty input.</param><param name="retainedBytes">Reports retained state bytes, including referenced values.</param>
    /// <param name="nullBehavior">The NULL input contract.</param><returns>The immutable aggregate.</returns>
    /// <exception cref="ArgumentException">The name is default/invalid, or a CLR type is unsupported.</exception>
    /// <exception cref="ArgumentNullException">A callback is null.</exception>
    public static GraphAggregateFunction Create<TState, T1, TResult>(GraphFunctionName name, Func<TState> seed,
        Func<TState, T1, TState> step, Func<TState, TResult> finish, Func<TState, long> retainedBytes,
        GraphNullBehavior nullBehavior = GraphNullBehavior.Strict)
        => new GraphTypedAggregateFunction<TState, T1, TResult>(name, seed, step, finish, retainedBytes, nullBehavior);
}

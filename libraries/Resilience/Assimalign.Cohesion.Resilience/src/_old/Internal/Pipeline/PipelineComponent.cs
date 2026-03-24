using System;
using System.Diagnostics;
using System.Threading.Tasks;

namespace Assimalign.Cohesion.Resilience.Internal.Pipeline;

/// <summary>
/// Represents a single component of a resilience pipeline.
/// </summary>
/// <remarks>
/// The component of the pipeline can be either a strategy, a generic strategy or a whole pipeline.
/// </remarks>
internal abstract class PipelineComponent : IAsyncDisposable
{
    public static PipelineComponent Empty { get; } = new NullComponent();

    internal ResilienceStrategyOptions? Options { get; set; }

    internal abstract ValueTask<OutcomeO<TResult>> ExecuteAsync<TResult, TState>(
        Func<ResilienceContextO, TState, ValueTask<OutcomeO<TResult>>> callback,
        ResilienceContextO context,
        TState state);

    internal TResult Execute<TResult, TState>(
        Func<ResilienceContextO, TState, TResult> callback,
        ResilienceContextO context,
        TState state)
        => ExecuteAsync([DebuggerDisableUserUnhandledExceptions] static (context, state) =>
        {
            try
            {
                return new ValueTask<OutcomeO<TResult>>(new OutcomeO<TResult>(state.callback(context, state.state)));
            }
#pragma warning disable CA1031 // Do not catch general exception types
            catch (Exception e)
#pragma warning restore CA1031
            {
                return new ValueTask<OutcomeO<TResult>>(new OutcomeO<TResult>(e));
            }
        },
        context, (callback, state)).GetResult().GetResultOrRethrow();

    public abstract ValueTask DisposeAsync();

    private sealed class NullComponent : PipelineComponent
    {
        internal override ValueTask<OutcomeO<TResult>> ExecuteAsync<TResult, TState>(Func<ResilienceContextO, TState, ValueTask<OutcomeO<TResult>>> callback, ResilienceContextO context, TState state)
            => callback(context, state);

        public override ValueTask DisposeAsync() => default;
    }
}

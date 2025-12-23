using System;
using System.Diagnostics.CodeAnalysis;
using System.Runtime.CompilerServices;
using System.Threading.Tasks;

namespace Assimalign.Cohesion.Resilience.Internal.Pipeline;

/// <summary>
/// A component that delegates the execution to the next component in the chain.
/// </summary>
internal sealed class DelegatingComponent : PipelineComponent
{
    private readonly PipelineComponent _component;

    public DelegatingComponent(PipelineComponent component) => _component = component;

    public PipelineComponent? Next { get; set; }

    public override ValueTask DisposeAsync() => default;

    [ExcludeFromCodeCoverage]
    internal override ValueTask<OutcomeO<TResult>> ExecuteAsync<TResult, TState>(
        Func<ResilienceContextO, TState, ValueTask<OutcomeO<TResult>>> callback,
        ResilienceContextO context,
        TState state) =>
        RuntimeFeature.IsDynamicCodeSupported ? ExecuteComponent(callback, context, state) : ExecuteComponentAot(callback, context, state);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static ValueTask<OutcomeO<TResult>> ExecuteNext<TResult, TState>(
        PipelineComponent next,
        Func<ResilienceContextO, TState, ValueTask<OutcomeO<TResult>>> callback,
        ResilienceContextO context,
        TState state)
    {
        if (context.CancellationToken.IsCancellationRequested)
        {
            return OutcomeO.FromExceptionAsValueTask<TResult>(new OperationCanceledException(context.CancellationToken).TrySetStackTrace());
        }

        return next.ExecuteAsync(callback, context, state);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal ValueTask<OutcomeO<TResult>> ExecuteComponent<TResult, TState>(
        Func<ResilienceContextO, TState, ValueTask<OutcomeO<TResult>>> callback,
        ResilienceContextO context,
        TState state)
        => _component.ExecuteAsync(
                static (context, state) => ExecuteNext(state.Next!, state.callback, context, state.state),
                context,
                (Next, callback, state));

#if NET6_0_OR_GREATER
    // Custom state object is used to cast the callback and state to prevent infinite
    // generic type recursion warning IL3054 when referenced in a native AoT application.
    // See https://github.com/App-vNext/Polly/issues/1732 for further context.
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal ValueTask<OutcomeO<TResult>> ExecuteComponentAot<TResult, TState>(
        Func<ResilienceContextO, TState, ValueTask<OutcomeO<TResult>>> callback,
        ResilienceContextO context,
        TState state) =>
        _component.ExecuteAsync(
            static (context, wrapper) =>
            {
                var callback = (Func<ResilienceContextO, TState, ValueTask<OutcomeO<TResult>>>)wrapper.Callback;
                var state = (TState)wrapper.State;
                return ExecuteNext(wrapper.Next, callback, context, state);
            },
            context,
            new StateWrapper(Next!, callback, state!));

    private readonly record struct StateWrapper(PipelineComponent Next, object Callback, object State);
#endif
}

using System;
using System.Collections.Generic;
using System.Threading.Tasks;

namespace Assimalign.Cohesion.Resilience.Internal.Pipeline;

internal sealed class ComponentWithDisposeCallbacks : PipelineComponent
{
    private readonly List<Action> _callbacks;

    public ComponentWithDisposeCallbacks(PipelineComponent component, List<Action> callbacks)
    {
        Component = component;
        _callbacks = callbacks;
    }

    internal PipelineComponent Component { get; }

    public override ValueTask DisposeAsync()
    {
        ExecuteCallbacks();

        return Component.DisposeAsync();
    }

    internal override ValueTask<OutcomeO<TResult>> ExecuteAsync<TResult, TState>(
        Func<ResilienceContextO, TState, ValueTask<OutcomeO<TResult>>> callback,
        ResilienceContextO context,
        TState state) => Component.ExecuteAsync(callback, context, state);

    private void ExecuteCallbacks()
    {
        foreach (var callback in _callbacks)
        {
            callback();
        }

        _callbacks.Clear();
    }
}

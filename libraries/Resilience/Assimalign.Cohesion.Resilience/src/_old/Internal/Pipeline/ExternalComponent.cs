using System;
using System.Diagnostics;
using System.Threading.Tasks;

namespace Assimalign.Cohesion.Resilience.Internal.Pipeline;

[DebuggerDisplay("{Component}")]
internal sealed class ExternalComponent : PipelineComponent
{
    public ExternalComponent(PipelineComponent component) => Component = component;

    internal PipelineComponent Component { get; }

    internal override ValueTask<OutcomeO<TResult>> ExecuteAsync<TResult, TState>(
        Func<ResilienceContextO, TState, ValueTask<OutcomeO<TResult>>> callback,
        ResilienceContextO context,
        TState state) => Component.ExecuteAsync(callback, context, state);

    public override ValueTask DisposeAsync() => default; // Don't dispose component that is external
}

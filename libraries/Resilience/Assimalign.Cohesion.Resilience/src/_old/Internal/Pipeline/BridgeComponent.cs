using System;
using System.Diagnostics;
using System.Threading.Tasks;

namespace Assimalign.Cohesion.Resilience.Internal.Pipeline;

[DebuggerDisplay("{Strategy}")]
internal sealed class BridgeComponent : BridgeComponentBase
{
    public BridgeComponent(ResilienceStrategy strategy)
        : base(strategy) => Strategy = strategy;

    public ResilienceStrategy Strategy { get; }

    internal override ValueTask<OutcomeO<TResult>> ExecuteAsync<TResult, TState>(
        Func<ResilienceContextO, TState, ValueTask<OutcomeO<TResult>>> callback,
        ResilienceContextO context,
        TState state) => Strategy.ExecuteAsync(callback, context, state);
}
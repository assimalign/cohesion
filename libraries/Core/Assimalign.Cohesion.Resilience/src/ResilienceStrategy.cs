using System;
using System.Threading.Tasks;

namespace Assimalign.Cohesion.Resilience;

/// <summary>
/// Base class for all proactive resilience strategies.
/// </summary>
public abstract class ResilienceStrategy : IResilienceStrategy
{
    public abstract ValueTask ExecuteAsync<TState>(
        ResilienceStrategyCallback<TState> callback,
        IResilienceContext context,
        TState state);
}

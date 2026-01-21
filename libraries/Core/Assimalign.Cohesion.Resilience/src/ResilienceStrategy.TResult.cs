using System.Threading.Tasks;

namespace Assimalign.Cohesion.Resilience;

public abstract class ResilienceStrategy<TResult> : IResilienceStrategy<TResult>
{
    public abstract ValueTask<TResult> ExecuteAsync<TState>(
        ResilienceStrategyCallback<TResult, TState> callback,
        IResilienceContext context,
        TState state);
}

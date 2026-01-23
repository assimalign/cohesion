using System.Threading.Tasks;

namespace Assimalign.Cohesion.Resilience.Internal;

internal class StrategyWrapper<TResult> : ResilienceStrategy<TResult>
{
    private readonly IResilienceStrategy<TResult> _strategy;

    public StrategyWrapper(IResilienceStrategy<TResult> strategy)
    {
        _strategy = strategy;
    }
    public override ValueTask<TResult> ExecuteAsync<TState>(ResilienceStrategyCallback<TResult, TState> callback, IResilienceContext context, TState state)
    {
        return _strategy.ExecuteAsync(callback, context, state);
    }
}

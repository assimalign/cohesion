using System.Threading.Tasks;

namespace Assimalign.Cohesion.Resilience.Internal;

internal class StrategyWrapper : ResilienceStrategy
{
    private readonly IResilienceStrategy _strategy;

    public StrategyWrapper(IResilienceStrategy strategy)
    {
        _strategy = strategy;
    }
    public override ValueTask ExecuteAsync<TState>(ResilienceStrategyCallback<TState> callback, IResilienceContext context, TState state)
    {
        return _strategy.ExecuteAsync(callback, context, state);
    }
}

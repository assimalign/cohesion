using System;
using System.Threading.Tasks;

namespace Assimalign.Cohesion.Resilience.Internal;

internal class DelegatingStrategy : ResilienceStrategy, IAsyncDisposable
{
    private readonly ResilienceStrategy _strategy;

    public DelegatingStrategy(ResilienceStrategy strategy)
    {
        _strategy = strategy;
    }

    public ResilienceStrategy? Next { get; set; }

    public override ValueTask ExecuteAsync<TState>(
        ResilienceStrategy<TState> callback, 
        IResilienceContext context, 
        TState state)
    {
        return _strategy.ExecuteAsync<TState>(
            async (context, state) =>
            {
                await Next!.ExecuteAsync<TState>(callback, context, state);

                return true;
            },
            context,
            state);
    }

    public ValueTask DisposeAsync()
    {
        return ValueTask.CompletedTask;
    }
}

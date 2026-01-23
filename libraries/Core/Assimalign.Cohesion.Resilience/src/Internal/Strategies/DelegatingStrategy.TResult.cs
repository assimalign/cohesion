using System;
using System.Threading.Tasks;

namespace Assimalign.Cohesion.Resilience.Internal;

internal class DelegatingStrategy<TResult> : ResilienceStrategy<TResult>, IAsyncDisposable
{
    private readonly ResilienceStrategy<TResult> _strategy;

    public DelegatingStrategy(ResilienceStrategy<TResult> strategy)
    {
        _strategy = strategy;
    }

    public ResilienceStrategy<TResult>? Next { get; set; }

    public override ValueTask<TResult> ExecuteAsync<TState>(
        ResilienceStrategy<TResult, TState> callback,
        IResilienceContext context,
        TState state)
    {
        return _strategy.ExecuteAsync<TState>(
            async (context, state) =>
            {
                return await Next!.ExecuteAsync<TState>(callback, context, state);
            },
            context,
            state);
    }

    public ValueTask DisposeAsync()
    {
        return ValueTask.CompletedTask;
    }
}

using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;

namespace Assimalign.Cohesion.Resilience.Internal;

internal class CompositeStrategy<TResult> : ResilienceStrategy<TResult>, IAsyncDisposable
{
    private readonly ResilienceStrategy<TResult> _strategy;

    public CompositeStrategy(IReadOnlyList<ResilienceStrategy<TResult>> strategies)
    {
        _strategy = Compose(strategies);
    }

    public override ValueTask<TResult> ExecuteAsync<TState>(
        ResilienceStrategy<TResult, TState> callback,
        IResilienceContext context,
        TState state)
    {
        return _strategy.ExecuteAsync<TState>(
            callback,
            context,
            state);
    }

    public ValueTask DisposeAsync()
    {
        return ValueTask.CompletedTask;
    }

    private static ResilienceStrategy<TResult> Compose(IReadOnlyList<ResilienceStrategy<TResult>> strategies)
    {
        if (strategies.Count == 1)
        {
            return strategies[0];
        }

        DelegatingStrategy<TResult>[] delegates = [.. strategies
            .Take(strategies.Count - 1)
            .Select(static item => new DelegatingStrategy<TResult>(item))];

        delegates[^1].Next = strategies[^1];

        // link the remaining ones
        for (var i = 0; i < delegates.Length - 1; i++)
        {
            delegates[i].Next = delegates[i + 1];
        }

        return delegates[0];
    }
}
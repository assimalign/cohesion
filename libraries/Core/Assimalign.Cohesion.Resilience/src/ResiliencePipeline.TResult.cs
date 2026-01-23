using System;
using System.Threading.Tasks;

namespace Assimalign.Cohesion.Resilience;

using Assimalign.Cohesion.Resilience.Internal;

public sealed class ResiliencePipeline<TResult> : IResiliencePipeline<TResult>
{
    private readonly ResilienceStrategy<TResult> _strategy;
    private readonly ResilienceContextPool _pool = ResilienceContextPool.Shared;


    internal ResiliencePipeline(ResilienceStrategy<TResult> strategy)
    {
        _strategy = strategy;
    }

    public ValueTask<TResult> ExecuteAsync<TState>(
        ResilienceCallback<TResult> callback,
        TState state)
    {
        ResilienceContext context = _pool.Rent();

        try
        {
            return (this as IResiliencePipeline<TResult>).ExecuteAsync(
                callback,
                context,
                state);
        }
        finally
        {
            _pool.Return(context);
        }
    }

    async ValueTask<TResult> IResiliencePipeline<TResult>.ExecuteAsync(
       ResilienceCallback<TResult> callback,
       IResilienceContext context,
       object? state)
    {
        Outcome<TResult> outcome = await _strategy
            .Invoke(callback, context, state)
            .ConfigureAwait(context.ContinueOnCapturedContext);

        outcome.ThrowIfException();

        return (TResult)outcome;
    }
}

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
        ResiliencePipelineCallback<TResult, TState> callback,
        TState state)
    {
        ResilienceContext context = _pool.Rent();

        try
        {
            return (this as IResiliencePipeline<TResult>).ExecuteAsync<TState>(
                callback,
                context,
                state);
        }
        finally
        {
            _pool.Return(context);
        }
    }

    ValueTask<TResult> IResiliencePipeline<TResult>.ExecuteAsync<TState>(
       ResiliencePipelineCallback<TResult, TState> callback,
       IResilienceContext context,
       TState state)
    {
        return _strategy.ExecuteAsync<TState>(
            async (context, state) =>
            {
                Outcome<TResult> outcome;

                try
                {
                    outcome = await callback.Invoke(context, state);
                }
                catch (Exception exception)
                {
                    outcome = exception;
                }

                outcome.ThrowIfException();

                return outcome;
            },
            context,
            state);
    }
}

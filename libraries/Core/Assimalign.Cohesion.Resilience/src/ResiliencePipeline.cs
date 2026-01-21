using System;
using System.Threading.Tasks;

namespace Assimalign.Cohesion.Resilience;

using Internal;

public sealed partial class ResiliencePipeline : IResiliencePipeline
{
    private readonly ResilienceContextPool _pool = ResilienceContextPool.Shared;
    private readonly ResilienceStrategy _strategy;

    internal ResiliencePipeline(ResilienceStrategy strategy)
    {
        _strategy = strategy;
    }

    public ValueTask ExecuteAsync<TState>(
        ResiliencePipelineCallback<TState> callback,
        TState state)
    {
        ResilienceContext context = _pool.Rent(false);

        try
        {
            return (this as IResiliencePipeline).ExecuteAsync<TState>(
                callback,
                context,
                state);
        }
        finally
        {
            _pool.Return(context);
        }
    }

    ValueTask IResiliencePipeline.ExecuteAsync<TState>(
        ResiliencePipelineCallback<TState> callback,
        IResilienceContext context,
        TState state)
    {
        return _strategy.ExecuteAsync<TState>(
            async (context, state) =>
            {
                Outcome outcome;

                try
                {
                    await callback.Invoke(context, state);
                }
                catch (Exception exception)
                {
                    outcome = exception;
                }

                return (outcome = true);
            },
            context,
            state);
    }
}
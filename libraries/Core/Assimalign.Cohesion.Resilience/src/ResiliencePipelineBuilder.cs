using System;
using System.Threading.Tasks;
using System.Collections.Generic;

namespace Assimalign.Cohesion.Resilience;

public sealed class ResiliencePipelineBuilder : IResiliencePipelineBuilder
{
    private readonly List<Func<ResilienceStrategy, ResilienceStrategy>> _strategies;

    public ResiliencePipelineBuilder()
    {
        _strategies = new List<Func<ResilienceStrategy, ResilienceStrategy>>();
    }

    public ResiliencePipelineBuilder UseStrategy(IResilienceStrategy strategy)
    {
        ArgumentNullException.ThrowIfNull(strategy);

        return UseStrategy(async (callback, context, state, next) =>
        {
            Outcome outcome = await strategy.ExecuteAsync(callback, context, state);

            if (!outcome.IsSuccess)
            {
                outcome = await next.Invoke(callback, context, state);
            }

            return outcome;
        });
    }

    public ResiliencePipelineBuilder UseStrategy(
        Func<ResilienceCallback, IResilienceContext, object?, ResilienceStrategy, ValueTask<Outcome>> strategy)
    {
        ArgumentNullException.ThrowIfNull(strategy);

        Func<ResilienceCallback, IResilienceContext, object?, ResilienceStrategy, ValueTask<Outcome>> strategy2 = strategy;

        (this as IResiliencePipelineBuilder).UseStrategy((ResilienceStrategy next) => (ResilienceCallback callback, IResilienceContext context, object? state) =>
        {
            return strategy2.Invoke(callback, context, state, next);
        });

        return this;
    }

    public ResiliencePipeline Build()
    {
        var strategy = new ResilienceStrategy(async (callback, context, state) =>
        {
            try
            {
                await callback.Invoke(context, state);

                return true;
            }
            catch (Exception exception)
            {
                return exception;
            }
        });

        for (int i = _strategies.Count - 1; i >= 0; i--)
        {
            strategy = _strategies[i].Invoke(strategy);
        }

        return new ResiliencePipeline(strategy);
    }

    IResiliencePipelineBuilder IResiliencePipelineBuilder.UseStrategy(Func<ResilienceStrategy, ResilienceStrategy> strategy)
    {
        ArgumentNullException.ThrowIfNull(strategy);
        _strategies.Add(strategy);
        return this;
    }

    IResiliencePipeline IResiliencePipelineBuilder.Build()
    {
        return Build();
    }
}

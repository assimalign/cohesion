using System;
using System.Threading.Tasks;
using System.Collections.Generic;
using System.Text;

namespace Assimalign.Cohesion.Resilience;

public sealed class ResiliencePipelineBuilder<TResult> : IResiliencePipelineBuilder<TResult>
{
    private readonly List<Func<ResilienceStrategy<TResult>, ResilienceStrategy<TResult>>> _strategies;

    public ResiliencePipelineBuilder()
    {
        _strategies = new List<Func<ResilienceStrategy<TResult>, ResilienceStrategy<TResult>>>();
    }

    public ResiliencePipelineBuilder<TResult> UseStrategy(IResilienceStrategy<TResult> strategy)
    {
        ArgumentNullException.ThrowIfNull(strategy);

        return UseStrategy(async (callback, context, state, next) =>
        {
            Outcome<TResult> outcome = await strategy.ExecuteAsync(callback, context, state);

            if (!outcome.IsSuccess)
            {
                outcome = await next.Invoke(callback, context, state);
            }

            return outcome;
        });
    }

    public ResiliencePipelineBuilder<TResult> UseStrategy(
        Func<ResilienceCallback<TResult>, IResilienceContext, object?, ResilienceStrategy<TResult>, ValueTask<Outcome<TResult>>> strategy)
    {
        ArgumentNullException.ThrowIfNull(strategy);

        Func<ResilienceCallback<TResult>, IResilienceContext, object?, ResilienceStrategy<TResult>, ValueTask<Outcome<TResult>>> strategy2 = strategy;

        (this as IResiliencePipelineBuilder<TResult>).UseStrategy((ResilienceStrategy<TResult> next) => (ResilienceCallback<TResult> callback, IResilienceContext context, object? state) =>
        {
            return strategy2.Invoke(callback, context, state, next);
        });

        return this;
    }

    public ResiliencePipeline<TResult> Build()
    {
        var strategy = new ResilienceStrategy<TResult>(async (callback, context, state) =>
        {
            try
            {
                return await callback.Invoke(context, state);
            }
            catch(Exception exception)
            {
                return exception;
            } 
        });

        for (int i = _strategies.Count - 1; i >= 0; i--)
        {
            strategy = _strategies[i].Invoke(strategy);
        }

        return new ResiliencePipeline<TResult>(strategy);
    }

    IResiliencePipeline<TResult> IResiliencePipelineBuilder<TResult>.Build()
    {
        return Build();
    }
    IResiliencePipelineBuilder<TResult> IResiliencePipelineBuilder<TResult>.UseStrategy(Func<ResilienceStrategy<TResult>, ResilienceStrategy<TResult>> strategy)
    {
        ArgumentNullException.ThrowIfNull(strategy);
        _strategies.Add(strategy);
        return this;
    }
}

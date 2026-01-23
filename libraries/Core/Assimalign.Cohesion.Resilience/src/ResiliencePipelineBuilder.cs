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

    /// <summary>
    /// Adds a custom resilience strategy to the pipeline. The specified strategy is executed before any subsequent
    /// strategies in the pipeline.
    /// </summary>
    /// <remarks>If the provided strategy does not indicate success, the next strategy in the pipeline is
    /// executed. This method enables advanced customization of the pipeline's behavior by allowing integration of
    /// user-defined strategies.</remarks>
    /// <param name="strategy">The resilience strategy to add to the pipeline. Cannot be null.</param>
    /// <returns>The current <see cref="ResiliencePipelineBuilder"/> instance for method chaining.</returns>
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
        if (_strategies.Count == 0)
        {
            throw new InvalidOperationException("");
        }

        ResilienceStrategy strategy = new ResilienceStrategy((callback, context, state) =>
        {
            throw new InvalidOperationException("The pipeline ");
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

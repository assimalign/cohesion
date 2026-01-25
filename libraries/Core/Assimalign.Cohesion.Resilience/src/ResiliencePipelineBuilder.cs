using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using static System.Net.Mime.MediaTypeNames;

namespace Assimalign.Cohesion.Resilience;

using Internal;

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

        return UseStrategy(strategy.ExecuteAsync);
    }

    /// <summary>
    /// 
    /// </summary>
    /// <param name="strategy"></param>
    /// <returns></returns>
    public ResiliencePipelineBuilder UseStrategy(ResilienceStrategy strategy)
    {
        ArgumentNullException.ThrowIfNull(strategy);

        ResilienceStrategy strategy2 = strategy;

        (this as IResiliencePipelineBuilder).UseStrategy((ResilienceStrategy next) => async (ResilienceCallback callback, IResilienceContext context, object? state) =>
        {
            Outcome first = await strategy2.Invoke(callback, context, state);

            if (!first.IsFailure(out Exception? firstException))
            {
                return first;
            }

            // Let's invoke the next strategy in the pipeline and check the outcome for end of pipeline exception
            Outcome second = await next.Invoke(callback, context, state);

            // If at the end of the pipeline we need to check for the Resilience exception
            if (second.IsFailure(out Exception? secondException))
            {
                if (secondException is ResilienceException ex && ex.Code == ResilienceErrorCode.PipelineFailure)
                {
                    if (first.IsFailure<ResilienceException>(out var alreadyWrapped))
                    {
                        return new ResilienceException(ResilienceErrorCode.PipelineFailure, "", alreadyWrapped); ;
                    }
                    else
                    {
                        return new ResilienceException(ResilienceErrorCode.PipelineFailure, "", 
                            new ResilienceException(ResilienceErrorCode.StrategyFailure, "", firstException));
                    }
                }

                if (second.IsFailure<ResilienceException>(out var _))
                {
                    return second;
                }
                else
                {
                    return new ResilienceException(ResilienceErrorCode.StrategyFailure, "", secondException);
                }
            }

            return second;
        });

        return this;
    }

    public ResiliencePipeline Build()
    {
        InvalidOperationException.ThrowIf(_strategies.Count == 0, "No strategies where added to the pipeline.");

        ResilienceStrategy strategy = new ResilienceStrategy((_, _, _) =>
            ValueTask.FromResult(new Outcome(new ResilienceException(
                ResilienceErrorCode.PipelineFailure,
                "The pipeline has reach the end. No successful strategy was applied to the callback."))));

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

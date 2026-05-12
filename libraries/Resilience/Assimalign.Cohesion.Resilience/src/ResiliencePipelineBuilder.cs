using System;
using System.Threading.Tasks;
using System.Collections.Generic;

namespace Assimalign.Cohesion.Resilience;

using Internal;
using Properties;

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
    public IResiliencePipelineBuilder UseStrategy(IResilienceStrategy strategy)
    {
        return UseStrategy(ArgumentNullException.ThrowIfNull<IResilienceStrategy>(strategy).ExecuteAsync);
    }

    /// <summary>
    /// Adds a resilience strategy to the pipeline, allowing it to participate in the execution flow and handle outcomes
    /// or failures as part of the pipeline sequence.
    /// </summary>
    /// <remarks>If the provided strategy fails, the pipeline will attempt to invoke the next strategy in the
    /// sequence. If all strategies fail, a <see cref="ResilienceException"/> with error code <see
    /// cref="ResilienceErrorCode.PipelineFailure"/> is returned, aggregating the encountered failures. This method
    /// enables composing multiple strategies to build complex resilience behaviors.</remarks>
    /// <param name="strategy">The resilience strategy to add to the pipeline. Cannot be null.</param>
    /// <returns>The current <see cref="ResiliencePipelineBuilder"/> instance for method chaining.</returns>
    public IResiliencePipelineBuilder UseStrategy(ResilienceStrategy strategy)
    {
        ResilienceStrategy strategy1 = ArgumentNullException.ThrowIfNull<ResilienceStrategy>(strategy);

        return UseStrategy((ResilienceStrategy strategy2) => async (ResilienceCallback callback, IResilienceContext context, object? state) =>
        {
            Outcome outcome1 = await strategy1.Invoke(callback, context, state);

            if (!outcome1.IsFailure(out Exception? exception1))
            {
                return outcome1;
            }

            // Let's invoke the next strategy in the pipeline and check the outcome for end of pipeline exception
            Outcome outcome2 = await strategy2.Invoke(callback, context, state);

            // If at the end of the pipeline we need to check for the Resilience exception
            if (!outcome2.IsFailure(out Exception? exception2))
            {
                return outcome2;
            }

            // Check if reached end of pipeline
            if (exception2 is ResilienceException exception2_1 && exception2_1.Code == ResilienceErrorCode.PipelineFailure)
            {
                ResilienceException exception1_1 = default!;

                // Wrap first exception if not already wrapped
                if (exception1 is ResilienceException exception1_2)
                {
                    exception1_1 = exception1_2;
                }
                else
                {
                    exception1_1 = ResilienceException.StrategyFailure(context.OperationKey, exception1);
                }

                // Spread other failures
                Exception[] failures = { exception1_1 };

                if (exception2_1.InnerException is AggregateException aggregate)
                {
                    failures = [.. failures, .. aggregate.InnerExceptions];
                }

                return ResilienceException.PipelineFailure(context.OperationKey, new AggregateException(failures));
            }

            return exception2;
        });
    }

    /// <summary>
    /// 
    /// </summary>
    /// <param name="strategy"></param>
    /// <returns></returns>
    public IResiliencePipelineBuilder UseStrategy(Func<ResilienceStrategy, ResilienceStrategy> strategy)
    {
        ArgumentNullException.ThrowIfNull(strategy);
        _strategies.Add(strategy);
        return this;
    }

    /// <summary>
    /// 
    /// </summary>
    /// <returns></returns>
    public IResiliencePipeline Build()
    {
        InvalidOperationException.ThrowIf(_strategies.Count == 0, "No strategies where added to the pipeline.");

        ResilienceStrategy strategy = new ResilienceStrategy((_, context, _) =>
        {
            return ValueTask.FromResult(new Outcome(ResilienceException.PipelineFailure(context.OperationKey)));
        });

        for (int i = _strategies.Count - 1; i >= 0; i--)
        {
            strategy = _strategies[i].Invoke(strategy);
        }

        return new ResiliencePipeline(strategy);
    }
}
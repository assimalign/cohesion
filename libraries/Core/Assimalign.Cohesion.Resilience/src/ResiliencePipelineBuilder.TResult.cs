using System;
using System.Collections.Generic;
using System.Threading.Tasks;

namespace Assimalign.Cohesion.Resilience;

using Internal;

public sealed class ResiliencePipelineBuilder<TResult> : IResiliencePipelineBuilder<TResult>
{
    private readonly List<Func<ResilienceStrategy<TResult>, ResilienceStrategy<TResult>>> _strategies;

    public ResiliencePipelineBuilder()
    {
        _strategies = new List<Func<ResilienceStrategy<TResult>, ResilienceStrategy<TResult>>>();
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
    public IResiliencePipelineBuilder<TResult> UseStrategy(IResilienceStrategy<TResult> strategy)
    {
        ArgumentNullException.ThrowIfNull(strategy);

        return UseStrategy(strategy.ExecuteAsync);
    }

    /// <summary>
    /// 
    /// </summary>
    /// <param name="strategy"></param>
    /// <returns></returns>
    public IResiliencePipelineBuilder<TResult> UseStrategy(ResilienceStrategy<TResult> strategy)
    {
        ResilienceStrategy<TResult> strategy1 = ArgumentNullException.ThrowIfNull<ResilienceStrategy<TResult>>(strategy);

        return UseStrategy((ResilienceStrategy<TResult> next) => async (ResilienceCallback<TResult> callback, IResilienceContext context, object? state) =>
        {
            Outcome<TResult> first = await strategy1.Invoke(callback, context, state);

            if (!first.IsFailure(out Exception? exception1))
            {
                return first;
            }

            // Let's invoke the next strategy in the pipeline and check the outcome for end of pipeline exception
            Outcome<TResult> second = await next.Invoke(callback, context, state);

            // If at the end of the pipeline we need to check for the Resilience exception
            if (!second.IsFailure(out Exception? exception2))
            {
                return second;
            }

            // Check if reached end of pipeline
            if (exception2 is ResilienceException exception2_1 && exception2_1.Code == ResilienceErrorCode.PipelineFailure)
            {
                ResilienceException exception1_1 = default!;

                // Wrap first exception in 
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

        return this;
    }

    /// <summary>
    /// 
    /// </summary>
    /// <param name="strategy"></param>
    /// <returns></returns>
    public IResiliencePipelineBuilder<TResult> UseStrategy(Func<ResilienceStrategy<TResult>, ResilienceStrategy<TResult>> strategy)
    {
        ArgumentNullException.ThrowIfNull(strategy);
        _strategies.Add(strategy);
        return this;
    }

    /// <summary>
    /// 
    /// </summary>
    /// <returns></returns>
    public IResiliencePipeline<TResult> Build()
    {
        InvalidOperationException.ThrowIf(_strategies.Count == 0, "No strategies where added to the pipeline.");

        ResilienceStrategy<TResult> strategy = new ResilienceStrategy<TResult>((_, context, _) =>
        {
            return ValueTask.FromResult(new Outcome<TResult>(ResilienceException.PipelineFailure(context.OperationKey)));
        });

        for (int i = _strategies.Count - 1; i >= 0; i--)
        {
            strategy = _strategies[i].Invoke(strategy);
        }

        return new ResiliencePipeline<TResult>(strategy);
    }
}

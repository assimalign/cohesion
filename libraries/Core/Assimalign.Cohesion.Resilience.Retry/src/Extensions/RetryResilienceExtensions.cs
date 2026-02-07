using System;

namespace Assimalign.Cohesion.Resilience;

using Internal;

public static class RetryResilienceExtensions
{
    extension(IResiliencePipelineBuilder builder)
    {
        public IResiliencePipelineBuilder UseRetry(Action<RetryStrategyOptions> configure)
        {
            RetryStrategyOptions options = new RetryStrategyOptions();

            ArgumentNullException.ThrowIfNull<Action<RetryStrategyOptions>>(configure).Invoke(options);

            return builder.UseStrategy(new RetryResilienceStrategy(options));
        }
    }

    extension<TResult>(IResiliencePipelineBuilder<TResult> builder)
    {
        public IResiliencePipelineBuilder<TResult> UseRetry(Action<RetryStrategyOptions<TResult>> configure)
        {
            RetryStrategyOptions<TResult> options = new RetryStrategyOptions<TResult>();

            ArgumentNullException.ThrowIfNull<Action<RetryStrategyOptions<TResult>>>(configure).Invoke(options);

            return builder.UseStrategy(new RetryResilienceStrategy<TResult>(options));
        }
    }
}

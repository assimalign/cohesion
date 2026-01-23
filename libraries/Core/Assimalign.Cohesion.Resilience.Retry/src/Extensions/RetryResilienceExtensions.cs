using System;

namespace Assimalign.Cohesion.Resilience;

using Internal;

public static class RetryResilienceExtensions
{
    extension(ResiliencePipelineBuilder builder)
    {
        public ResiliencePipelineBuilder UseRetry(Action<RetryStrategyOptions> configure)
        {
            RetryStrategyOptions options = new RetryStrategyOptions();

            ArgumentNullException.ThrowIfNull<Action<RetryStrategyOptions>>(configure).Invoke(options);

            return builder.UseStrategy(new RetryResilienceStrategy(options));
        }
    }

    extension<TResult>(ResiliencePipelineBuilder<TResult> builder)
    {
        public ResiliencePipelineBuilder<TResult> UseRetry(Action<RetryStrategyOptions<TResult>> configure)
        {
            RetryStrategyOptions<TResult> options = new RetryStrategyOptions<TResult>();

            ArgumentNullException.ThrowIfNull<Action<RetryStrategyOptions<TResult>>>(configure).Invoke(options);

            return builder.UseStrategy(new RetryResilienceStrategy<TResult>(options));
        }
    }
}

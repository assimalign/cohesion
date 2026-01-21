using System;
using System.Collections.Generic;
using System.Text;

namespace Assimalign.Cohesion.Resilience;

using Internal;

public static class RetryExtensions
{
    extension(ResiliencePipelineBuilder builder)
    {
        public ResiliencePipelineBuilder UseRetry(Action<RetryStrategyOptions> configure)
        {
            RetryStrategyOptions options = new RetryStrategyOptions();

            ArgumentNullException.ThrowIfNull<Action<RetryStrategyOptions>>(configure).Invoke(options);

            return builder.UseStrategy(new RetryStrategy(options));
        }
    }

    extension<TResult>(ResiliencePipelineBuilder<TResult> builder)
    {
        public ResiliencePipelineBuilder<TResult> UseRetry(Action<RetryStrategyOptions<TResult>> configure)
        {
            RetryStrategyOptions<TResult> options = new RetryStrategyOptions<TResult>();

            ArgumentNullException.ThrowIfNull<Action<RetryStrategyOptions<TResult>>>(configure).Invoke(options);

            return builder.UseStrategy(new RetryStrategy<TResult>(options));
        }
    }
}

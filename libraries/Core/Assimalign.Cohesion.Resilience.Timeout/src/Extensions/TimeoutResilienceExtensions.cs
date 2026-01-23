using System;

namespace Assimalign.Cohesion.Resilience;

using Internal;

public static class TimeoutResilienceExtensions
{
    extension(ResiliencePipelineBuilder builder)
    {
        public ResiliencePipelineBuilder UseTimeout(Action<TimeoutStrategyOptions> configure)
        {
            TimeoutStrategyOptions options = new TimeoutStrategyOptions();

            ArgumentNullException.ThrowIfNull<Action<TimeoutStrategyOptions>>(configure).Invoke(options);

            return builder.UseStrategy(new TimeoutResilienceStrategy(options));
        }
    }

    extension<TResult>(ResiliencePipelineBuilder<TResult> builder)
    {
        public ResiliencePipelineBuilder<TResult> UseTimeout(Action<TimeoutStrategyOptions> configure)
        {
            TimeoutStrategyOptions options = new TimeoutStrategyOptions();

            ArgumentNullException.ThrowIfNull<Action<TimeoutStrategyOptions>>(configure).Invoke(options);

            return builder.UseStrategy(new TimeoutResilienceStrategy<TResult>(options));
        }
    }
}

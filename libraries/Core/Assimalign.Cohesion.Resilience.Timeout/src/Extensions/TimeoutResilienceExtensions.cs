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

            (builder as IResiliencePipelineBuilder).UseStrategy(new TimeoutResilienceStrategy(options));

            return builder;
        }
    }

    extension<TResult>(ResiliencePipelineBuilder<TResult> builder)
    {
        public ResiliencePipelineBuilder<TResult> UseTimeout(Action<TimeoutStrategyOptions> configure)
        {
            TimeoutStrategyOptions options = new TimeoutStrategyOptions();

            ArgumentNullException.ThrowIfNull<Action<TimeoutStrategyOptions>>(configure).Invoke(options);

            (builder as IResiliencePipelineBuilder<TResult>).UseStrategy(new TimeoutResilienceStrategy<TResult>(options));

            return builder;
        }
    }
}

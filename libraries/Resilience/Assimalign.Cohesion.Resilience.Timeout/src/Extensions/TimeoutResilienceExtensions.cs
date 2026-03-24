using System;

namespace Assimalign.Cohesion.Resilience;

using Internal;

/// <summary>
/// Adds timeout resilience strategies to pipeline builders.
/// </summary>
public static class TimeoutResilienceExtensions
{
    extension(IResiliencePipelineBuilder builder)
    {
        /// <summary>
        /// Adds a timeout strategy to the pipeline builder.
        /// </summary>
        /// <param name="configure">The timeout configuration delegate.</param>
        /// <returns>The current pipeline builder.</returns>
        public IResiliencePipelineBuilder UseTimeout(Action<TimeoutStrategyOptions> configure)
        {
            TimeoutStrategyOptions options = new();

            ArgumentNullException.ThrowIfNull(configure);
            configure.Invoke(options);

            return builder.UseStrategy(new TimeoutResilienceStrategy(options));
        }
    }

    extension<TResult>(IResiliencePipelineBuilder<TResult> builder)
    {
        /// <summary>
        /// Adds a timeout strategy to the generic pipeline builder.
        /// </summary>
        /// <param name="configure">The timeout configuration delegate.</param>
        /// <returns>The current pipeline builder.</returns>
        public IResiliencePipelineBuilder<TResult> UseTimeout(Action<TimeoutStrategyOptions> configure)
        {
            TimeoutStrategyOptions options = new();

            ArgumentNullException.ThrowIfNull(configure);
            configure.Invoke(options);

            return builder.UseStrategy(new TimeoutResilienceStrategy<TResult>(options));
        }
    }
}

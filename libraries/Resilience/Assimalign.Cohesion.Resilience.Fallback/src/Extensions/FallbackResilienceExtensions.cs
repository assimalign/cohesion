using System;

namespace Assimalign.Cohesion.Resilience;

using Internal;

/// <summary>
/// Adds fallback resilience strategies to pipeline builders.
/// </summary>
public static class FallbackResilienceExtensions
{
    extension(IResiliencePipelineBuilder builder)
    {
        /// <summary>
        /// Adds a non-generic fallback strategy to the pipeline builder.
        /// </summary>
        /// <param name="configure">The fallback configuration delegate.</param>
        /// <returns>The current pipeline builder.</returns>
        public IResiliencePipelineBuilder UseFallback(Action<FallbackStrategyOptions> configure)
        {
            FallbackStrategyOptions options = new();

            ArgumentNullException.ThrowIfNull(configure);
            configure.Invoke(options);

            return builder.UseStrategy(new FallbackResilienceStrategy(options));
        }
    }

    extension<TResult>(IResiliencePipelineBuilder<TResult> builder)
    {
        /// <summary>
        /// Adds a generic fallback strategy to the pipeline builder.
        /// </summary>
        /// <param name="configure">The fallback configuration delegate.</param>
        /// <returns>The current pipeline builder.</returns>
        public IResiliencePipelineBuilder<TResult> UseFallback(Action<FallbackStrategyOptions<TResult>> configure)
        {
            FallbackStrategyOptions<TResult> options = new();

            ArgumentNullException.ThrowIfNull(configure);
            configure.Invoke(options);

            return builder.UseStrategy(new FallbackResilienceStrategy<TResult>(options));
        }
    }
}

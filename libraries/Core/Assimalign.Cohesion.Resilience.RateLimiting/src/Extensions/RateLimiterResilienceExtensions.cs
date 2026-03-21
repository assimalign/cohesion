using System;

namespace Assimalign.Cohesion.Resilience;

using Internal;

/// <summary>
/// Adds rate limiter strategies to pipeline builders.
/// </summary>
public static class RateLimiterResilienceExtensions
{
    extension(IResiliencePipelineBuilder builder)
    {
        /// <summary>
        /// Adds a rate limiter strategy to the pipeline builder.
        /// </summary>
        /// <param name="configure">The rate limiter configuration delegate.</param>
        /// <returns>The current pipeline builder.</returns>
        public IResiliencePipelineBuilder UseRateLimiter(Action<RateLimiterStrategyOptions> configure)
        {
            RateLimiterStrategyOptions options = new();

            ArgumentNullException.ThrowIfNull(configure);
            configure.Invoke(options);

            return builder.UseStrategy(new RateLimitingResilienceStrategy(options));
        }
    }

    extension<TResult>(IResiliencePipelineBuilder<TResult> builder)
    {
        /// <summary>
        /// Adds a rate limiter strategy to the generic pipeline builder.
        /// </summary>
        /// <param name="configure">The rate limiter configuration delegate.</param>
        /// <returns>The current pipeline builder.</returns>
        public IResiliencePipelineBuilder<TResult> UseRateLimiter(Action<RateLimiterStrategyOptions> configure)
        {
            RateLimiterStrategyOptions options = new();

            ArgumentNullException.ThrowIfNull(configure);
            configure.Invoke(options);

            return builder.UseStrategy(new RateLimitingResilienceStrategy<TResult>(options));
        }
    }
}

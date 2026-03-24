using System;

namespace Assimalign.Cohesion.Resilience;

using Internal;

/// <summary>
/// Adds circuit breaker strategies to pipeline builders.
/// </summary>
public static class CircuitBreakerResilienceExtensions
{
    extension(IResiliencePipelineBuilder builder)
    {
        /// <summary>
        /// Adds a circuit breaker strategy to the pipeline builder.
        /// </summary>
        /// <param name="configure">The circuit breaker configuration delegate.</param>
        /// <returns>The current pipeline builder.</returns>
        public IResiliencePipelineBuilder UseCircuitBreaker(Action<CircuitBreakerStrategyOptions> configure)
        {
            CircuitBreakerStrategyOptions options = new();

            ArgumentNullException.ThrowIfNull(configure);
            configure.Invoke(options);

            return builder.UseStrategy(new CircuitBreakerResilienceStrategy(options));
        }
    }

    extension<TResult>(IResiliencePipelineBuilder<TResult> builder)
    {
        /// <summary>
        /// Adds a circuit breaker strategy to the generic pipeline builder.
        /// </summary>
        /// <param name="configure">The circuit breaker configuration delegate.</param>
        /// <returns>The current pipeline builder.</returns>
        public IResiliencePipelineBuilder<TResult> UseCircuitBreaker(Action<CircuitBreakerStrategyOptions> configure)
        {
            CircuitBreakerStrategyOptions options = new();

            ArgumentNullException.ThrowIfNull(configure);
            configure.Invoke(options);

            return builder.UseStrategy(new CircuitBreakerResilienceStrategy<TResult>(options));
        }
    }
}

using System;

namespace Assimalign.Cohesion.Resilience;

using Internal;

/// <summary>
/// Adds hedging strategies to pipeline builders.
/// </summary>
public static class HedgingResilienceExtensions
{
    extension(IResiliencePipelineBuilder builder)
    {
        /// <summary>
        /// Adds a hedging strategy to the pipeline builder.
        /// </summary>
        /// <param name="configure">The hedging configuration delegate.</param>
        /// <returns>The current pipeline builder.</returns>
        public IResiliencePipelineBuilder UseHedging(Action<HedgingStrategyOptions> configure)
        {
            HedgingStrategyOptions options = new();

            ArgumentNullException.ThrowIfNull(configure);
            configure.Invoke(options);

            return builder.UseStrategy(new HedgingResilienceStrategy(options));
        }
    }

    extension<TResult>(IResiliencePipelineBuilder<TResult> builder)
    {
        /// <summary>
        /// Adds a hedging strategy to the generic pipeline builder.
        /// </summary>
        /// <param name="configure">The hedging configuration delegate.</param>
        /// <returns>The current pipeline builder.</returns>
        public IResiliencePipelineBuilder<TResult> UseHedging(Action<HedgingStrategyOptions> configure)
        {
            HedgingStrategyOptions options = new();

            ArgumentNullException.ThrowIfNull(configure);
            configure.Invoke(options);

            return builder.UseStrategy(new HedgingResilienceStrategy<TResult>(options));
        }
    }
}

using System;
using System.Threading.RateLimiting;
using System.Threading.Tasks;

namespace Assimalign.Cohesion.Resilience;

/// <summary>
/// Configures a rate limiter strategy for a resilience pipeline.
/// </summary>
public sealed class RateLimiterStrategyOptions
{
    /// <summary>
    /// Gets or sets the rate limiter used to acquire execution permits.
    /// </summary>
    public RateLimiter? RateLimiter { get; set; }

    /// <summary>
    /// Gets or sets the number of permits required for each execution.
    /// </summary>
    public int PermitCount { get; set; } = 1;

    /// <summary>
    /// Gets or sets the callback invoked when the rate limiter rejects an execution.
    /// </summary>
    public Func<OnRateLimiterRejectedArguments, ValueTask>? OnRejected { get; set; }
}

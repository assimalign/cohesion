using System;

namespace Assimalign.Cohesion.Resilience;

/// <summary>
/// Represents the arguments emitted when the rate limiter rejects an execution.
/// </summary>
public readonly struct OnRateLimiterRejectedArguments
{
    /// <summary>
    /// Initializes a new instance of the <see cref="OnRateLimiterRejectedArguments"/> struct.
    /// </summary>
    /// <param name="context">The execution context.</param>
    /// <param name="permitCount">The number of permits requested.</param>
    /// <param name="retryAfter">The server-provided retry delay, if one exists.</param>
    public OnRateLimiterRejectedArguments(
        IResilienceContext context,
        int permitCount,
        TimeSpan? retryAfter)
    {
        Context = context;
        PermitCount = permitCount;
        RetryAfter = retryAfter;
    }

    /// <summary>
    /// Gets the execution context.
    /// </summary>
    public IResilienceContext Context { get; }

    /// <summary>
    /// Gets the number of permits requested by the execution.
    /// </summary>
    public int PermitCount { get; }

    /// <summary>
    /// Gets the retry delay suggested by the rate limiter, if one exists.
    /// </summary>
    public TimeSpan? RetryAfter { get; }
}

using System;

namespace Assimalign.Cohesion.Resilience;

/// <summary>
/// Represents a rate limiter rejection.
/// </summary>
public sealed class RateLimiterRejectedException : ExecutionRejectedException
{
    /// <summary>
    /// Initializes a new instance of the <see cref="RateLimiterRejectedException"/> class.
    /// </summary>
    /// <param name="message">The rejection message.</param>
    /// <param name="retryAfter">The retry delay suggested by the rate limiter, if one exists.</param>
    public RateLimiterRejectedException(string message, TimeSpan? retryAfter = null)
        : base(message)
    {
        RetryAfter = retryAfter;
    }

    /// <summary>
    /// Gets the retry delay suggested by the rate limiter, if one exists.
    /// </summary>
    public TimeSpan? RetryAfter { get; }
}

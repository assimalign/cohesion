using System;

namespace Assimalign.Cohesion.Resilience;

/// <summary>
/// Represents a rejection caused by an open circuit breaker.
/// </summary>
public sealed class BrokenCircuitException : ExecutionRejectedException
{
    /// <summary>
    /// Initializes a new instance of the <see cref="BrokenCircuitException"/> class.
    /// </summary>
    /// <param name="message">The rejection message.</param>
    /// <param name="retryAfter">The amount of time remaining before another execution may be attempted.</param>
    public BrokenCircuitException(string message, TimeSpan retryAfter)
        : base(message)
    {
        RetryAfter = retryAfter;
    }

    /// <summary>
    /// Gets the amount of time remaining before another execution may be attempted.
    /// </summary>
    public TimeSpan RetryAfter { get; }
}

using System;

namespace Assimalign.Cohesion.Resilience;

/// <summary>
/// Represents the arguments used to determine whether the circuit breaker should handle a failure.
/// </summary>
public readonly struct CircuitBreakerPredicateArguments
{
    /// <summary>
    /// Initializes a new instance of the <see cref="CircuitBreakerPredicateArguments"/> struct.
    /// </summary>
    /// <param name="context">The execution context.</param>
    /// <param name="exception">The failure that occurred.</param>
    /// <param name="failureCount">The failure count that would be recorded if the failure is handled.</param>
    /// <param name="state">The breaker state associated with the execution.</param>
    public CircuitBreakerPredicateArguments(
        IResilienceContext context,
        Exception exception,
        int failureCount,
        CircuitBreakerState state)
    {
        Context = context;
        Exception = exception;
        FailureCount = failureCount;
        State = state;
    }

    /// <summary>
    /// Gets the execution context.
    /// </summary>
    public IResilienceContext Context { get; }

    /// <summary>
    /// Gets the failure that occurred.
    /// </summary>
    public Exception Exception { get; }

    /// <summary>
    /// Gets the failure count that would be recorded if the failure is handled.
    /// </summary>
    public int FailureCount { get; }

    /// <summary>
    /// Gets the breaker state associated with the execution.
    /// </summary>
    public CircuitBreakerState State { get; }
}

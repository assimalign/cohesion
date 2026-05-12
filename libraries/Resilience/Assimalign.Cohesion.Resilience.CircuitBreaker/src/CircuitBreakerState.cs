namespace Assimalign.Cohesion.Resilience;

/// <summary>
/// Represents the current state of a circuit breaker.
/// </summary>
public enum CircuitBreakerState
{
    /// <summary>
    /// The circuit is allowing executions.
    /// </summary>
    Closed,

    /// <summary>
    /// The circuit is rejecting executions.
    /// </summary>
    Open,

    /// <summary>
    /// The circuit is allowing a probe execution to determine whether it can close.
    /// </summary>
    HalfOpen,
}

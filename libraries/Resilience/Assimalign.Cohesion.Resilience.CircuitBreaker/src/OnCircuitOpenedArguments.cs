using System;

namespace Assimalign.Cohesion.Resilience;

/// <summary>
/// Represents the arguments emitted when a circuit breaker opens.
/// </summary>
public readonly struct OnCircuitOpenedArguments
{
    /// <summary>
    /// Initializes a new instance of the <see cref="OnCircuitOpenedArguments"/> struct.
    /// </summary>
    /// <param name="context">The execution context.</param>
    /// <param name="exception">The failure that opened the circuit.</param>
    /// <param name="failureCount">The handled failure count.</param>
    /// <param name="breakDuration">The amount of time the circuit will remain open.</param>
    public OnCircuitOpenedArguments(
        IResilienceContext context,
        Exception exception,
        int failureCount,
        TimeSpan breakDuration)
    {
        Context = context;
        Exception = exception;
        FailureCount = failureCount;
        BreakDuration = breakDuration;
    }

    /// <summary>
    /// Gets the execution context.
    /// </summary>
    public IResilienceContext Context { get; }

    /// <summary>
    /// Gets the failure that opened the circuit.
    /// </summary>
    public Exception Exception { get; }

    /// <summary>
    /// Gets the handled failure count.
    /// </summary>
    public int FailureCount { get; }

    /// <summary>
    /// Gets the amount of time the circuit will remain open.
    /// </summary>
    public TimeSpan BreakDuration { get; }
}

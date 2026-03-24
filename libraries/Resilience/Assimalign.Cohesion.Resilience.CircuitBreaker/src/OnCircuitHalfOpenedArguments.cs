using System;

namespace Assimalign.Cohesion.Resilience;

/// <summary>
/// Represents the arguments emitted when a circuit breaker transitions to half-open.
/// </summary>
public readonly struct OnCircuitHalfOpenedArguments
{
    /// <summary>
    /// Initializes a new instance of the <see cref="OnCircuitHalfOpenedArguments"/> struct.
    /// </summary>
    /// <param name="context">The execution context.</param>
    /// <param name="breakDuration">The break duration that elapsed before the probe was allowed.</param>
    public OnCircuitHalfOpenedArguments(IResilienceContext context, TimeSpan breakDuration)
    {
        Context = context;
        BreakDuration = breakDuration;
    }

    /// <summary>
    /// Gets the execution context.
    /// </summary>
    public IResilienceContext Context { get; }

    /// <summary>
    /// Gets the break duration that elapsed before the probe was allowed.
    /// </summary>
    public TimeSpan BreakDuration { get; }
}

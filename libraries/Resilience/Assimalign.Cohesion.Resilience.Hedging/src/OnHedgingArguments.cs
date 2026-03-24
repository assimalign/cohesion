using System;

namespace Assimalign.Cohesion.Resilience;

/// <summary>
/// Represents the arguments emitted when an additional hedged attempt is scheduled.
/// </summary>
public readonly struct OnHedgingArguments
{
    /// <summary>
    /// Initializes a new instance of the <see cref="OnHedgingArguments"/> struct.
    /// </summary>
    /// <param name="context">The execution context.</param>
    /// <param name="attemptNumber">The zero-based attempt number being scheduled.</param>
    /// <param name="delay">The configured delay between attempts.</param>
    public OnHedgingArguments(IResilienceContext context, int attemptNumber, TimeSpan delay)
    {
        Context = context;
        AttemptNumber = attemptNumber;
        Delay = delay;
    }

    /// <summary>
    /// Gets the execution context.
    /// </summary>
    public IResilienceContext Context { get; }

    /// <summary>
    /// Gets the zero-based attempt number being scheduled.
    /// </summary>
    public int AttemptNumber { get; }

    /// <summary>
    /// Gets the configured delay between attempts.
    /// </summary>
    public TimeSpan Delay { get; }
}

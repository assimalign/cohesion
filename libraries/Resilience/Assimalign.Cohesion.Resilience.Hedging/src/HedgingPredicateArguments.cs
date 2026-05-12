using System;

namespace Assimalign.Cohesion.Resilience;

/// <summary>
/// Represents the arguments used to determine whether a failed hedged attempt should be handled.
/// </summary>
public readonly struct HedgingPredicateArguments
{
    /// <summary>
    /// Initializes a new instance of the <see cref="HedgingPredicateArguments"/> struct.
    /// </summary>
    /// <param name="context">The execution context.</param>
    /// <param name="exception">The failure produced by the attempt.</param>
    /// <param name="attemptNumber">The zero-based attempt number.</param>
    public HedgingPredicateArguments(IResilienceContext context, Exception exception, int attemptNumber)
    {
        Context = context;
        Exception = exception;
        AttemptNumber = attemptNumber;
    }

    /// <summary>
    /// Gets the execution context.
    /// </summary>
    public IResilienceContext Context { get; }

    /// <summary>
    /// Gets the failure produced by the attempt.
    /// </summary>
    public Exception Exception { get; }

    /// <summary>
    /// Gets the zero-based attempt number.
    /// </summary>
    public int AttemptNumber { get; }
}

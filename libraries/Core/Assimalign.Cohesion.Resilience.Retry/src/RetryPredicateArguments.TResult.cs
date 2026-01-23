namespace Assimalign.Cohesion.Resilience;

using Internal;

#pragma warning disable CA1815 // Override equals and operator equals on value types

/// <summary>
/// Represents the arguments used by <see cref="RetryStrategyOptions{TResult}.ShouldRetry"/> for determining whether a retry should be performed.
/// </summary>
/// <typeparam name="TResult">The type of result.</typeparam>
/// <remarks>
/// Always use the constructor when creating this struct, otherwise we do not guarantee binary compatibility.
/// </remarks>
public readonly struct RetryPredicateArguments<TResult> 
{
    /// <summary>
    /// Initializes a new instance of the <see cref="RetryPredicateArguments{TResult}"/> struct.
    /// </summary>
    /// <param name="outcome">The context in which the resilience operation or event occurred.</param>
    /// <param name="context">The outcome of the resilience operation or event.</param>
    /// <param name="attempts">The zero-based attempt number.</param>
    public RetryPredicateArguments(IResilienceContext context, Outcome<TResult> outcome, int attempts)
    {
        Context = context;
        Outcome = outcome;
        Attempts = attempts;
    }

    /// <summary>
    /// Gets the outcome of the user-specified callback.
    /// </summary>
    public Outcome<TResult> Outcome { get; }

    /// <summary>
    /// Gets the context of this event.
    /// </summary>
    public IResilienceContext Context { get; }

    /// <summary>
    /// Gets the zero-based attempt number.
    /// </summary>
    public int Attempts { get; }
}

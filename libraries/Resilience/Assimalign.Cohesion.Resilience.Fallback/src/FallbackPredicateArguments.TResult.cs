namespace Assimalign.Cohesion.Resilience;

/// <summary>
/// Represents the arguments used to determine whether a generic fallback should handle a failed outcome.
/// </summary>
/// <typeparam name="TResult">The callback result type.</typeparam>
public readonly struct FallbackPredicateArguments<TResult>
{
    /// <summary>
    /// Initializes a new instance of the <see cref="FallbackPredicateArguments{TResult}"/> struct.
    /// </summary>
    /// <param name="context">The execution context.</param>
    /// <param name="outcome">The outcome produced by the callback.</param>
    public FallbackPredicateArguments(IResilienceContext context, Outcome<TResult> outcome)
    {
        Context = context;
        Outcome = outcome;
    }

    /// <summary>
    /// Gets the execution context.
    /// </summary>
    public IResilienceContext Context { get; }

    /// <summary>
    /// Gets the outcome produced by the callback.
    /// </summary>
    public Outcome<TResult> Outcome { get; }
}

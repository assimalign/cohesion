namespace Assimalign.Cohesion.Resilience;

/// <summary>
/// Represents the arguments supplied to a generic fallback action.
/// </summary>
/// <typeparam name="TResult">The callback result type.</typeparam>
public readonly struct FallbackActionArguments<TResult>
{
    /// <summary>
    /// Initializes a new instance of the <see cref="FallbackActionArguments{TResult}"/> struct.
    /// </summary>
    /// <param name="context">The execution context.</param>
    /// <param name="outcome">The failed outcome being handled.</param>
    public FallbackActionArguments(IResilienceContext context, Outcome<TResult> outcome)
    {
        Context = context;
        Outcome = outcome;
    }

    /// <summary>
    /// Gets the execution context.
    /// </summary>
    public IResilienceContext Context { get; }

    /// <summary>
    /// Gets the failed outcome being handled.
    /// </summary>
    public Outcome<TResult> Outcome { get; }
}

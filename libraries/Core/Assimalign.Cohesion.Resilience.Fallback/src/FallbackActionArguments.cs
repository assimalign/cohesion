namespace Assimalign.Cohesion.Resilience;

/// <summary>
/// Represents the arguments supplied to a non-generic fallback action.
/// </summary>
public readonly struct FallbackActionArguments
{
    /// <summary>
    /// Initializes a new instance of the <see cref="FallbackActionArguments"/> struct.
    /// </summary>
    /// <param name="context">The execution context.</param>
    /// <param name="outcome">The failed outcome being handled.</param>
    public FallbackActionArguments(IResilienceContext context, Outcome outcome)
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
    public Outcome Outcome { get; }
}

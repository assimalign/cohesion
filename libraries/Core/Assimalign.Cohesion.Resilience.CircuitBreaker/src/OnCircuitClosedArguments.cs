namespace Assimalign.Cohesion.Resilience;

/// <summary>
/// Represents the arguments emitted when a circuit breaker closes.
/// </summary>
public readonly struct OnCircuitClosedArguments
{
    /// <summary>
    /// Initializes a new instance of the <see cref="OnCircuitClosedArguments"/> struct.
    /// </summary>
    /// <param name="context">The execution context.</param>
    public OnCircuitClosedArguments(IResilienceContext context)
    {
        Context = context;
    }

    /// <summary>
    /// Gets the execution context.
    /// </summary>
    public IResilienceContext Context { get; }
}

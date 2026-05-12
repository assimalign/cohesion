namespace Assimalign.Cohesion.Resilience;

/// <summary>
/// Arguments used by the timeout strategy to retrieve a timeout for current execution.
/// </summary>
/// <remarks>
/// Always use the constructor when creating this struct, otherwise we do not guarantee binary compatibility.
/// </remarks>
public readonly struct TimeoutGeneratorArguments
{
    /// <summary>
    /// Initializes a new instance of the <see cref="TimeoutGeneratorArguments"/> struct.
    /// </summary>
    /// <param name="context">The context associated with the execution of a user-provided callback.</param>
    public TimeoutGeneratorArguments(IResilienceContext context) => Context = context;

    /// <summary>
    /// Gets the context associated with the execution of a user-provided callback.
    /// </summary>
    public IResilienceContext Context { get; }
}
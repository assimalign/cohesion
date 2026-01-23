using System;

namespace Assimalign.Cohesion.Resilience;

/// <summary>
/// Arguments used by the timeout strategy to notify that a timeout occurred.
/// </summary>
/// <remarks>
/// Always use the constructor when creating this struct, otherwise we do not guarantee binary compatibility.
/// </remarks>
public readonly struct OnTimeoutArguments
{
    /// <summary>
    /// Initializes a new instance of the <see cref="OnTimeoutArguments"/> struct.
    /// </summary>
    /// <param name="context">The context associated with the execution of a user-provided callback.</param>
    /// <param name="timeout">The timeout value assigned.</param>
    public OnTimeoutArguments(IResilienceContext context, TimeSpan timeout)
    {
        Context = context;
        Timeout = timeout;
    }

    /// <summary>
    /// Gets the context associated with the execution of a user-provided callback.
    /// </summary>
    public IResilienceContext Context { get; }

    /// <summary>
    /// Gets the timeout value assigned.
    /// </summary>
    public TimeSpan Timeout { get; }
}
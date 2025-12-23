using System;

namespace Assimalign.Cohesion.Resilience;

/// <summary>
/// The validation context that encapsulates parameters for the validation.
/// </summary>
internal sealed class ResilienceValidationContext
{
    /// <summary>
    /// Initializes a new instance of the <see cref="ResilienceValidationContext"/> class.
    /// </summary>
    /// <param name="instance">The instance being validated.</param>
    /// <param name="primaryMessage">The primary validation message.</param>
    public ResilienceValidationContext(object instance, string primaryMessage)
    {
        Instance = ArgumentNullException.ThrowIfNull<object>(instance);
        PrimaryMessage = ArgumentNullException.ThrowIfNull<string>(primaryMessage);
    }

    /// <summary>
    /// Gets the instance being validated.
    /// </summary>
    public object Instance { get; }

    /// <summary>
    /// Gets the primary validation message.
    /// </summary>
    /// <remarks>
    /// The primary message is displayed first followed by the details about the validation errors.
    /// </remarks>
    public string PrimaryMessage { get; }
}


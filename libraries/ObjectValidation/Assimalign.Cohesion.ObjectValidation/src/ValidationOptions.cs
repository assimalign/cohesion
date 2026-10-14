namespace Assimalign.Cohesion.ObjectValidation;

/// <summary>
/// Options that control how a <see cref="Validator"/> evaluates its profiles: which failures it reports
/// (<see cref="ValidationMode"/> between items, <see cref="ContinueThroughValidationChain"/> within one
/// item's chain of rules), and whether a failure throws instead of returning a
/// <see cref="ValidationResult"/>.
/// </summary>
public sealed class ValidationOptions
{
    /// <summary>
    /// Default constructor for instantiating <see cref="ValidationOptions"/>.
    /// </summary>
    public ValidationOptions() { }


    /// <inheritdoc cref="IValidationContext.ThrowExceptionOnFailure"/>
    public bool ThrowExceptionOnFailure { get; set; }

    /// <inheritdoc cref="IValidationContext.ContinueThroughValidationChain"/>
    public bool ContinueThroughValidationChain { get; set; }

    /// <inheritdoc cref="IValidationContext.ValidationMode"/>
    public ValidationMode ValidationMode { get; set; }   
}
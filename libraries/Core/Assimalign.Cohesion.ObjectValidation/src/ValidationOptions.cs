namespace Assimalign.Cohesion.ObjectValidation;

/// <summary>
/// 
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
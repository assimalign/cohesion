namespace Assimalign.Cohesion.ObjectValidation;

/// <summary>
/// Specifies whether the validator should continue or stop on a validation failure.
/// <see cref="ValidationMode"/> is specific to an item being validate it 
/// does not indicate how many rules will run.
/// </summary>
public enum ValidationMode
{
    /// <summary>
    /// Tells the validator to continue through all <see cref="IValidationItem"/> failures.
    /// </summary>
    Cascade = 0,

    /// <summary>
    /// Tells the validator to stop validation after first failure.
    /// </summary>
    Stop = 1
}
namespace Assimalign.Cohesion.ObjectValidation;

/// <summary>
/// An abstraction of a validation error.
/// </summary>
public interface IValidationError
{
    /// <summary>
    /// A unique error code to use when the validation rule fails.
    /// </summary>
    string Code { get; set; } 

    /// <summary>
    /// A unique error message to use when the validation rule fails.
    /// </summary>
    string Message { get; set; }

    /// <summary>
    /// An identifier of the source of the validation error.
    /// </summary>
    string Source { get; set; }
}
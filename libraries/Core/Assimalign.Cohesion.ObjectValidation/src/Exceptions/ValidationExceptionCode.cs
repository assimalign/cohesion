namespace Assimalign.Cohesion.ObjectValidation;

/// <summary>
/// 
/// </summary>
public enum ValidationExceptionCode
{
    /// <summary>
    /// 
    /// </summary>
    UnknownError = unchecked(4000),
    /// <summary>
    /// 
    /// </summary>
    InvalidEvaluation = unchecked(4001),
    /// <summary>
    /// 
    /// </summary>
    InvalidValidationExpression = unchecked(4002),
    /// <summary>
    /// 
    /// </summary>
    InvalidValidationCast = unchecked(4003),
    /// <summary>
    /// An error that is thrown when creating a validator that has not been configured.
    /// </summary>
    InvalidValidatorCreation = unchecked(4004),

    /// <summary>
    /// An error that is thrown when the <see cref="IValidationRule"/> is not supported internally.
    /// </summary>
    UnsupportedValidationRule = unchecked(4005)
}


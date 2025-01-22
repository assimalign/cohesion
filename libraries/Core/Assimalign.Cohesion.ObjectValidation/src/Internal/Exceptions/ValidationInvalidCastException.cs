using System;


namespace Assimalign.Cohesion.ObjectValidation.Internal;

internal sealed class ValidationInvalidCastException : ValidationException
{
    public ValidationInvalidCastException(
        string message,
        InvalidCastException inner,
        string source = default) : base(message, inner)
    {
        base.ErrorCode = ValidationExceptionCode.InvalidValidationCast;
        base.Source = source;
        base.HResult = typeof(ValidationInvalidCastException).GetHashCode();
    }
}
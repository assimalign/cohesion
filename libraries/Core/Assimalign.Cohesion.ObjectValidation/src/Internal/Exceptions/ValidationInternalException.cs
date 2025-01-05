using System;

namespace Assimalign.Cohesion.ObjectValidation.Internal;

internal sealed class ValidationInternalException : ValidationException
{
    public ValidationInternalException(string message) : base(message)
    {
        base.ErrorCode = ValidationExceptionCode.UnknownError;
        base.HResult = nameof(ValidationInternalException).GetHashCode();
    }

    public ValidationInternalException(string message, string source) : this(message)
    {
        base.Source = source;
    }

    public ValidationInternalException(string message, Exception inner, string source = default) : base(message, inner)
    {
        base.Source = source;
    }
}
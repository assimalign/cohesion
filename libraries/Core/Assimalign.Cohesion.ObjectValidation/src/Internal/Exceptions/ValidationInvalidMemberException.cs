using System.Linq.Expressions;

namespace Assimalign.Cohesion.ObjectValidation.Internal;

/// <summary>
/// An exception when the passed Expression is not a MemberExpression 
/// </summary>
internal sealed class ValidationInvalidMemberException : ValidationException
{
    private const string message = "The following expression: {0} is not supported. Only MemberExpression's are supported for validation.";

    public ValidationInvalidMemberException(Expression invalidExpression) : base(string.Format(message, invalidExpression))
    {
        base.ErrorCode = ValidationExceptionCode.InvalidValidationExpression;
        base.Source = invalidExpression.ToString();
        base.HResult = typeof(ValidationInvalidMemberException).GetHashCode();
    }
}


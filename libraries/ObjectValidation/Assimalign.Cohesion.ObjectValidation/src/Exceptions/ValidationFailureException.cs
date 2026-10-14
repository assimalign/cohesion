using System;
using System.Collections.Generic;
using System.Linq;

namespace Assimalign.Cohesion.ObjectValidation;

/// <summary>
/// An Exception that is thrown when a validation failure occurs
/// </summary>
public sealed class ValidationFailureException : ValidationException
{

    internal ValidationFailureException(IValidationContext context) : this(context.Errors)
    {
    }

    internal ValidationFailureException(IEnumerable<IValidationError> errors) : base("")
    {
        Errors = Array.AsReadOnly(errors.ToArray());
    }

    /// <summary>
    /// The validation failures supplied at construction, in their original order. The collection is a
    /// read-only snapshot; the error objects themselves are retained without copying.
    /// </summary>
    public IReadOnlyList<IValidationError> Errors { get; }
}


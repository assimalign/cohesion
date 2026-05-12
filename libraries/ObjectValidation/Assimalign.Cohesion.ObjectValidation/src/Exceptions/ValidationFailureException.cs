using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace Assimalign.Cohesion.ObjectValidation;

/// <summary>
/// An Exception that is thrown when a validation failure occurs
/// </summary>
public sealed class ValidationFailureException : ValidationException
{

    internal ValidationFailureException(IValidationContext context) : base("")
    {

    }

    internal ValidationFailureException(IEnumerable<IValidationError> errors) : base("")
    {

    }

}


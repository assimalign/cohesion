using System;
using System.Linq.Expressions;

namespace Assimalign.Cohesion.ObjectMapping.Internal.Exceptions;

internal sealed class MapperInvalidMappingException : MapperException
{
    private const string message = "The target expression: '{0}' has already been mapped.";
    public MapperInvalidMappingException(Expression expression) 
        : base(string.Format(message, expression))
    {

    }
}

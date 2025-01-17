using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace Assimalign.Cohesion.ObjectMapping.Internal.Exceptions;

internal sealed class MapperInvalidContextException : MapperException
{
    private const string message = "The MapperContext of type '{0}' and type '{1}' and does not match the action types of '{2}' and '{3}'.";


    public MapperInvalidContextException(Type contextSourceType, Type contextTargetType, Type actionSourceType, Type actionTargetType)
        : base(string.Format(message, contextSourceType.Name, contextTargetType.Name, actionSourceType.Name, actionTargetType.Name))
    {

    }


}

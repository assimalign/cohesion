using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace Assimalign.Cohesion.ObjectMapping.Internal.Exceptions;

internal class MapperInstanceCreationException : MapperException
{
    private const string message = "Unable to create instance of type: '{0}'.";
    
    public MapperInstanceCreationException(Type type, Exception inner = null) 
        : base(string.Format(message, type.Name), inner)
    {

    }

   
}

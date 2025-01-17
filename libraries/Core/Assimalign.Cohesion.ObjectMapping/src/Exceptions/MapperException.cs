using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace Assimalign.Cohesion.ObjectMapping;

public abstract class MapperException : Exception
{
    public MapperException(string message) : base(message)
    {

    }

    public MapperException(string message, Exception inner): base(message, inner)
    {

    }
}

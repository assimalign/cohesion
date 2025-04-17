using System;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace Assimalign.Cohesion.Http;

public readonly struct Route
{

    public Route(string value)
    {
        
    }



    public static implicit operator Route(string value)
    {
        return new Route(value);
    }
}

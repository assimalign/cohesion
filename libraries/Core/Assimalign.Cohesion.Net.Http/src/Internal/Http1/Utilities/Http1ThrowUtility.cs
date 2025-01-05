using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace Assimalign.Cohesion.Net.Http.Internal;

internal static class Http1ThrowUtility
{


    public static void ThrowIf<TException>(Func<bool> method) where TException : Exception, new()
    {
        if (method.Invoke())
        {

        }
    }
}

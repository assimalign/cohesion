using System;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace Assimalign.Cohesion.Internal;

using Http.Internal;

internal static partial class ThrowHelper
{

    public static void InvalidHttpPath(string message) => 
        throw new HttpInvalidPathException(message);
    internal static void InvalidHttpMethod(string method) => 
        throw new HttpInvalidMethodException($"The provided method is invalid: '{method}'. A method can only contain alphanumeric characters.");
}

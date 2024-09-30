using System;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace Assimalign.Cohesion.FileSystem.Internal;

internal static class ThrowHelper
{
    [DoesNotReturn]
    internal static void ThrowArgumentNullException(string paramName)
    {
        throw new ArgumentNullException(paramName);
    }

    [DoesNotReturn]
    internal static void ThrowArgumentException(string message)
    {
        throw new ArgumentException(message);
    }
}

using System;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;

namespace Assimalign.Cohesion.Internal;

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

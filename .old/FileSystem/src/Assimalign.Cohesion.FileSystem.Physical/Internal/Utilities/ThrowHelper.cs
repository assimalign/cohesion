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
    public static void ThrowPlatformNotSupportedException()
    {
        throw new PlatformNotSupportedException();
    }

    [DoesNotReturn]
    public static void ThrowInvalidOperationException(string message)
    {
        throw new InvalidOperationException(message);
    }
}

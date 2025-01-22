using System;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace Assimalign.Cohesion.Internal;

internal static partial class ThrowHelper
{
    [DoesNotReturn]
    public static void ThrowPlatformNotSupportedException()
    {
        throw new PlatformNotSupportedException();
    }
}

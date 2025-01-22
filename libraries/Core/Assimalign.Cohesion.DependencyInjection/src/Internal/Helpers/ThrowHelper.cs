using System;
using System.Diagnostics.CodeAnalysis;
using System.Runtime.CompilerServices;

namespace Assimalign.Cohesion.DependencyInjection.Internal;

internal static class ThrowHelper
{
    [DoesNotReturn]
    [MethodImpl(MethodImplOptions.NoInlining)]
    internal static void ThrowObjectDisposedException()
    {
        throw new ObjectDisposedException(nameof(IServiceProvider));
    }

    [DoesNotReturn]
    internal static void ThrowArgumentExceptionIfNull(string paramName)
    {
        throw new ArgumentNullException(paramName);
    } 
}

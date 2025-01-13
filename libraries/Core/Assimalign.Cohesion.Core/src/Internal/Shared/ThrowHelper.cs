using System;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;

namespace Assimalign.Cohesion.Internal;

internal static partial class ThrowHelper
{
    internal static void ThrowIfArgumentNull(object value, string paramName)
    {
        if (value is null)
        {
            throw new ArgumentNullException("");
        }
    }
    internal static void ThrowIfArgumentNullOrEmpty(string? value, string paramName)
    {
        if (string.IsNullOrEmpty(value))
        {
            throw new ArgumentNullException($"The {paramName} cannot be null or empty.");
        }
    }

    internal static void ThrowArgumentExceptionIf([DoesNotReturnIf(true)]bool condition, string message)
    {
        if (condition)
        {
            throw new ArgumentException("");
        }
    }

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

    [DoesNotReturn]
    internal static void ThrowInvalidOperationException(string message)
    {
        throw new InvalidOperationException(message);
    }

}

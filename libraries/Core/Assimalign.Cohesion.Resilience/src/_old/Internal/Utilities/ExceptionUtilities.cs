using System;

namespace Assimalign.Cohesion.Resilience.Internal;

#pragma warning disable S3011 // Reflection should not be used to increase accessibility of classes, methods, or fields

internal static class ExceptionUtilities
{
    public static T TrySetStackTrace<T>(this T exception)
        where T : Exception
    {
        if (!string.IsNullOrWhiteSpace(exception.StackTrace))
        {
            return exception;
        }

        return exception;
    }
}

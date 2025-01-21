using System;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.Text.Json;

namespace Assimalign.Cohesion.Internal;

internal static partial class ThrowHelper
{
    #region Arguments

    internal static void ThrowIfNull(object? value, string paramName)
    {
        if (value is null || (value is string str && string.IsNullOrEmpty(str)))
        {
            throw GetArgumentNullException(paramName);
        }
    }

    [DoesNotReturn]
    internal static void ThrowArgumentNullException(string paramName) =>
        throw GetArgumentNullException(paramName);

    [DoesNotReturn]
    internal static void ThrowArgumentNullException(string paramName, string message) =>
        throw GetArgumentNullException(paramName, message);

    [DoesNotReturn]
    internal static void ThrowArgumentException(string message) =>
        throw GetArgumentException(message);

    [DoesNotReturn]
    internal static void ThrowArgumentException(string message, string paramName) =>
        throw GetArgumentException(message, paramName);
    
    [DoesNotReturn]
    internal static void ThrowInvalidOperationException(string message) =>
        throw new InvalidOperationException(message);
    
    internal static ArgumentException GetArgumentException(string message) =>
        new ArgumentException(message);
    
    internal static ArgumentException GetArgumentException(string message, string paramName) =>
        new ArgumentException(message, paramName);

    internal static ArgumentNullException GetArgumentNullException(string paramName) =>
        new ArgumentNullException(paramName);

    internal static ArgumentNullException GetArgumentNullException(string paramName, string message) =>
        new ArgumentNullException(paramName, message);

    #endregion

    #region Threading

    [DoesNotReturn]
    internal static void ThrowObjectDisposedException(string objectName) =>
        throw GetObjectDisposedException(objectName);

    [DoesNotReturn]
    internal static void ThrowObjectDisposedException(string objectName, string message) =>
        throw GetObjectDisposedException(objectName, message);

    internal static ObjectDisposedException GetObjectDisposedException(string objectName) =>
        new ObjectDisposedException(objectName);

    internal static ObjectDisposedException GetObjectDisposedException(string objectName, string message) =>
        new ObjectDisposedException(objectName, message);

    #endregion

    #region Json Serialization

    [DoesNotReturn]
    internal static void ThrowJsonException(string message) =>
        throw GetJsonException(message);

    internal static JsonException GetJsonException(string message) =>
        new JsonException(message);

    #endregion
}

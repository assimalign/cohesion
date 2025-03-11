using System;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Text.Json;

namespace Assimalign.Cohesion.Internal;

internal static partial class ThrowHelper
{
    #region Arguments

    internal static object ThrowIfNull(
        [NotNull]object? argument,
        [CallerArgumentExpression(nameof(argument))] string? paramName = null)
    {
        if (argument is null)
        {
            ThrowArgumentNullException(paramName);
        }

        return argument;
    }

    internal static string ThrowIfNullOrEmpty(
        [NotNull] string? argument,
        [CallerArgumentExpression(nameof(argument))] string? paramName = null)
    {
        if (string.IsNullOrEmpty(argument))
        {
            ThrowArgumentNullException(paramName);
        }

        return argument;
    }

    internal static ICollection<T> ThrowIfNullOrNone<T>(
        [NotNull] ICollection<T> argument,
        [CallerArgumentExpression(nameof(argument))] string? paramName = null)
    {
        if (argument is null || !argument.Any())
        {
            ThrowArgumentNullException(paramName);
        }

        return argument;
    }

    internal static T[] ThrowIfNullOrNone<T>(
        [NotNull] T[] argument,
        [CallerArgumentExpression(nameof(argument))] string? paramName = null)
    {
        if (argument is null || argument.Length == 0)
        {
            ThrowArgumentNullException(paramName);
        }

        return argument;
    }

    [DoesNotReturn]
    internal static void ThrowArgumentNullException(string? paramName)
    {
        throw GetArgumentNullException(paramName);
    }

    [DoesNotReturn]
    internal static void ThrowArgumentNullException(string paramName, string message)
    {
        throw GetArgumentNullException(paramName, message);
    }

    [DoesNotReturn]
    internal static void ThrowArgumentException(string message)
    {
        throw GetArgumentException(message);
    }

    [DoesNotReturn]
    internal static void ThrowArgumentException(string message, string paramName)
    {
        throw GetArgumentException(message, paramName);
    }

    [DoesNotReturn]
    internal static void ThrowInvalidOperationException(string message)
    {
        throw new InvalidOperationException(message);
    }

    internal static ArgumentException GetArgumentException(string message)
    {
        return new ArgumentException(message);
    }

    internal static ArgumentException GetArgumentException(string message, string paramName)
    {
        return new ArgumentException(message, paramName);
    }

    internal static ArgumentNullException GetArgumentNullException(string? paramName)
    {
        return new ArgumentNullException(paramName);
    }

    internal static ArgumentNullException GetArgumentNullException(string paramName, string message)
    {
        return new ArgumentNullException(paramName, message);
    }

    #endregion

    #region Threading

    [DoesNotReturn]
    internal static void ThrowObjectDisposedException(string objectName)
    {
        throw GetObjectDisposedException(objectName);
    }

    [DoesNotReturn]
    internal static void ThrowObjectDisposedException(string objectName, string message)
    {
        throw GetObjectDisposedException(objectName, message);
    }

    internal static ObjectDisposedException GetObjectDisposedException(string objectName)
    {
        return new ObjectDisposedException(objectName);
    }

    internal static ObjectDisposedException GetObjectDisposedException(string objectName, string message)
    {
        return new ObjectDisposedException(objectName, message);
    }

    #endregion

    #region IO

    [DoesNotReturn]
    internal static void ThrowEndOfStreamException(string message)
    {
        throw GetEndOfStreamException(message);
    }

    internal static EndOfStreamException GetEndOfStreamException(string message)
    {
        return new EndOfStreamException(message);
    }

    #endregion

    #region Json Serialization

    [DoesNotReturn]
    internal static void ThrowJsonException(string message) =>
        throw GetJsonException(message);

    internal static JsonException GetJsonException(string message) =>
        new JsonException(message);

    #endregion
}

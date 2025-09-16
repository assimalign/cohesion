using System;
using System.IO;
using System.Text.Json;
using System.Collections;
using System.Diagnostics.CodeAnalysis;
using System.Runtime.CompilerServices;

namespace Assimalign.Cohesion.Internal;

internal static partial class ThrowHelper
{
    #region Arguments

    internal static T ThrowIfNull<T>(
        [NotNull]T? argument,
        [CallerArgumentExpression(nameof(argument))] string? paramName = null)
    {
        if (argument is null)
        {
            ThrowArgumentNullException(paramName);
        }

        return argument;
    }

    internal static ReadOnlySpan<T> ThrowIfEmptySpan<T>(
        [NotNull] ReadOnlySpan<T> argument,
        [CallerArgumentExpression(nameof(argument))] string? paramName = null) 
    {
        if (argument.Length <= 0)
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

    internal static T ThrowIfNullOrNone<T>(
        [NotNull] T argument,
        [CallerArgumentExpression(nameof(argument))] string? paramName = null) where T : IEnumerable
    {
        switch (argument)
        {
            case null:
            case ICollection collection when collection.Count == 0:
            case Array array when array.Length == 0:
                ThrowArgumentNullException(paramName);
                break;
        }

        return argument;
    }


    internal static T ThrowIfNotDefined<T>(
        [NotNull] T argument,
        [CallerArgumentExpression(nameof(argument))] string? paramName = null) where T : struct, Enum
    {
        if (!Enum.IsDefined(argument))
        {
            throw new ArgumentException($"The integral value does not exist in {typeof(T).Name}");
        }

        return argument;
    }

    internal static T ThrowIfNotType<T>(
        [NotNull] object argument,
        [CallerArgumentExpression(nameof(argument))] string? paramName = null)
    {
        if (argument is not T type)
        {
            throw new ArgumentException("");
        }

        return type;
    }

    [DoesNotReturn]
    internal static void ThrowArgumentNullException(
        string? paramName)
    {
        throw new ArgumentNullException(paramName);
    }

    [DoesNotReturn]
    internal static void ThrowArgumentNullException(
        string paramName, 
        string message)
    {
        throw new ArgumentNullException(paramName, message);
    }

    [DoesNotReturn]
    internal static void ThrowArgumentException(string message)
    {
        throw new ArgumentException(message);
    }

    [DoesNotReturn]
    internal static void ThrowArgumentException(string message, string paramName)
    {
        throw new ArgumentException(message, paramName);
    }

    [DoesNotReturn]
    internal static void ThrowInvalidOperationException(string message)
    {
        throw new InvalidOperationException(message);
    }

    #endregion

    #region Threading

    [DoesNotReturn]
    internal static void ThrowObjectDisposedException(string objectName)
    {
        throw new ObjectDisposedException(objectName);
    }

    [DoesNotReturn]
    internal static void ThrowObjectDisposedException(string objectName, string message)
    {
        throw new ObjectDisposedException(objectName, message);
    }

    #endregion

    #region IO

    [DoesNotReturn]
    internal static void ThrowEndOfStreamException(string message)
    {
        throw new EndOfStreamException(message);
    }

    #endregion

    #region Json Serialization

    [DoesNotReturn]
    internal static void ThrowJsonException(string message)
    {
        throw new JsonException(message);
    }

    [DoesNotReturn]
    internal static void ThrowPathToLongException()
    {
        throw new PathTooLongException();
    }

    #endregion
}

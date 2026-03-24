using System;
using System.IO;
using System.Diagnostics.CodeAnalysis;

namespace Assimalign.Cohesion.Internal;

internal static partial class ThrowHelper
{
    [DoesNotReturn]
    internal static void ThrowFileOrDirectoryAlreadyExists(FileSystemPath path, Exception? innerException = null)
    {
        Assimalign.Cohesion.FileSystem.FileSystemException.ThrowPathAlreadyExist(path, innerException);
    }

    [DoesNotReturn]
    internal static void ThrowAccessNotAllowed(FileSystemPath path, Exception? innerException = null)
    {
        Assimalign.Cohesion.FileSystem.FileSystemException.ThrowAccessDenied(path, innerException);
    }

    [DoesNotReturn]
    internal static void ThrowPathNotFound(FileSystemPath path, Exception? innerException = null)
    {
        throw new Assimalign.Cohesion.FileSystem.FileSystemException(
            Assimalign.Cohesion.FileSystem.FileSystemErrorCode.NotFound,
            $"The specified path was not found: '{path}'.",
            innerException);
    }

    [DoesNotReturn]
    internal static void ThrowPathTooLong(FileSystemPath path, Exception? innerException = null)
    {
        Assimalign.Cohesion.FileSystem.FileSystemException.ThrowPathTooLong(path, innerException);
    }

    [DoesNotReturn]
    internal static void ThrowNotEnoughSpace(Exception? innerException = null)
    {
        Assimalign.Cohesion.FileSystem.FileSystemException.ThrowNotEnoughSpace(innerException);
    }

    [DoesNotReturn]
    internal static void ThrowInvalidOperationException(string message)
    {
        throw new InvalidOperationException(message);
    }
}

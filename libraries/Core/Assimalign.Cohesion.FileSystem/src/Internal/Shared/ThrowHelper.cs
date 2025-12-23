using System;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.IO;
using System.Text;

namespace Assimalign.Cohesion.Internal;

using Assimalign.Cohesion.FileSystem;
using System.Runtime.CompilerServices;

internal static partial class ThrowHelper
{
    [DoesNotReturn]
    internal static void ThrowPathNotFound(
        [NotNull] FileSystemPath path,
        [AllowNull] Exception? innerException = null,
        [CallerArgumentExpression(nameof(path))] string? paramName = null)
    {
        var message = string.Format("The provided path does not exist `{0}`.", path);
        throw new FileSystemException(
            FileSystemErrorCode.NotFound,
            message,
            innerException);
    }

    [DoesNotReturn]
    internal static void ThrowPathTooLong(
        [NotNull] FileSystemPath path,
        [AllowNull] Exception? innerException = null,
        [CallerArgumentExpression(nameof(path))] string? paramName = null)
    {
        var message = string.Format("The provided path is too long `{0}`.", path);
        throw new FileSystemException(
            FileSystemErrorCode.PathTooLong,
            message,
            innerException);
    }

    [DoesNotReturn]
    internal static void ThrowAccessNotAllowed(
        [NotNull] FileSystemPath path,
        [AllowNull] Exception? innerException = null,
        [CallerArgumentExpression(nameof(path))] string? paramName = null)
    {
        var message = string.Format("Access to the path is denied `{0}`.", path);
        throw new FileSystemException(
            FileSystemErrorCode.AccessDenied,
            message,
            innerException);
    }

    [DoesNotReturn]
    internal static void ThrowFileOrDirectoryAlreadyExists(
        [NotNull] FileSystemPath path,
        [AllowNull] Exception? innerException = null,
        [CallerArgumentExpression(nameof(path))] string? paramName = null)
    {
        var message = string.Format("The file or directory already exists `{0}`.", path);
        throw new FileSystemException(
            FileSystemErrorCode.Conflict,
            message,
            innerException);
    }

    [DoesNotReturn]
    internal static void ThrowNotEnoughSpace(
        [AllowNull] Exception? innerException = null)
    {
        throw new FileSystemException(
            FileSystemErrorCode.NotEnoughSpace,
            "",
            innerException);
    }
}

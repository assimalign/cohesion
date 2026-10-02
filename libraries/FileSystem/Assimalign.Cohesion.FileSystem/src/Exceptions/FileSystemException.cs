using System;
using System.IO;
using System.Diagnostics.CodeAnalysis;

namespace Assimalign.Cohesion.FileSystem;

using Properties;

/// <summary>
/// Domain exception raised by Cohesion <see cref="IFileSystem"/> implementations. Carries a
/// <see cref="FileSystemErrorCode"/> so callers can branch on the failure mode without text
/// matching.
/// </summary>
public class FileSystemException : SystemException
{
    /// <summary>
    /// Initializes a new exception with <see cref="FileSystemErrorCode.Other"/>.
    /// </summary>
    public FileSystemException(string message)
        : this(FileSystemErrorCode.Other, message)
    {
    }

    /// <summary>
    /// Initializes a new exception with the supplied <paramref name="code"/>.
    /// </summary>
    public FileSystemException(FileSystemErrorCode code, string message)
        : base(message)
    {
        Code = code;
    }

    /// <summary>
    /// Initializes a new exception with the supplied <paramref name="code"/> and inner exception.
    /// </summary>
    public FileSystemException(FileSystemErrorCode code, string message, Exception? innerException)
        : base(message, innerException)
    {
        Code = code;
    }

    /// <summary>
    /// Diagnostic code attached to the exception.
    /// </summary>
    public virtual FileSystemErrorCode Code { get; } = FileSystemErrorCode.Other;

    /// <summary>
    /// Throws a <see cref="FileSystemException"/> indicating that the directory does not exist.
    /// </summary>
    [DoesNotReturn]
    public static void ThrowDirectoryNotFound(
        [NotNull] FileSystemPath path,
        [AllowNull] DirectoryNotFoundException? innerException = null)
    {
        var message = string.Format(ErrorMessages.DirectoryNotFound, path);
        throw new FileSystemException(
            code: FileSystemErrorCode.NotFound,
            message: message,
            innerException ?? new DirectoryNotFoundException(message));
    }

    /// <summary>
    /// Throws a <see cref="FileSystemException"/> indicating that the file does not exist.
    /// </summary>
    [DoesNotReturn]
    public static void ThrowFileNotFound(
        [NotNull] FileSystemPath path,
        [AllowNull] FileNotFoundException? innerException = null)
    {
        var message = string.Format(ErrorMessages.FileNotFound, path);
        throw new FileSystemException(
            FileSystemErrorCode.NotFound,
            message,
            innerException ?? new FileNotFoundException(message));
    }

    /// <summary>
    /// Throws a <see cref="FileSystemException"/> with <see cref="FileSystemErrorCode.NotFound"/>
    /// for callers that do not know whether the missing entry was a file or directory.
    /// </summary>
    [DoesNotReturn]
    public static void ThrowPathNotFound(
        [NotNull] FileSystemPath path,
        [AllowNull] Exception? innerException = null)
    {
        throw new FileSystemException(
            FileSystemErrorCode.NotFound,
            $"The specified path was not found: '{path}'.",
            innerException);
    }

    /// <summary>
    /// Throws a <see cref="FileSystemException"/> indicating that the path is too long.
    /// </summary>
    [DoesNotReturn]
    public static void ThrowPathTooLong(
        [NotNull] FileSystemPath path,
        [AllowNull] Exception? innerException = null)
    {
        var message = string.Format(ErrorMessages.PathTooLong, path);
        throw new FileSystemException(
            FileSystemErrorCode.PathTooLong,
            message,
            innerException ?? new PathTooLongException(message));
    }

    /// <summary>
    /// Throws a <see cref="FileSystemException"/> indicating that access to the path is denied.
    /// </summary>
    [DoesNotReturn]
    public static void ThrowAccessDenied(
        [NotNull] FileSystemPath path,
        [AllowNull] Exception? innerException = null)
    {
        var message = string.Format(ErrorMessages.AccessDeniedToPath, path);
        throw new FileSystemException(
            FileSystemErrorCode.AccessDenied,
            message,
            innerException ?? new UnauthorizedAccessException(message));
    }

    /// <summary>
    /// Throws a <see cref="FileSystemException"/> indicating that the path is held open by
    /// another consumer that has not granted compatible share access.
    /// </summary>
    [DoesNotReturn]
    public static void ThrowPathInUse(
        [NotNull] FileSystemPath path,
        [AllowNull] Exception? innerException = null)
    {
        var message = string.Format("The specified path is in use by another process: '{0}'.", path);
        throw new FileSystemException(
            FileSystemErrorCode.PathInUse,
            message,
            innerException);
    }

    /// <summary>
    /// Throws a <see cref="FileSystemException"/> indicating that the file or directory already
    /// exists at the target location.
    /// </summary>
    [DoesNotReturn]
    public static void ThrowPathAlreadyExist(
        [NotNull] FileSystemPath path,
        [AllowNull] Exception? innerException = null)
    {
        var message = string.Format("The file or directory already exists: '{0}'.", path);
        throw new FileSystemException(
            FileSystemErrorCode.Conflict,
            message,
            innerException);
    }

    /// <summary>
    /// Throws a <see cref="FileSystemException"/> indicating that the file system has run out
    /// of space.
    /// </summary>
    [DoesNotReturn]
    public static void ThrowNotEnoughSpace(
        [AllowNull] Exception? innerException = null)
    {
        throw new FileSystemException(
            FileSystemErrorCode.NotEnoughSpace,
            ErrorMessages.NotEnoughSpace,
            innerException);
    }

    /// <summary>
    /// Throws a <see cref="FileSystemException"/> with <see cref="FileSystemErrorCode.ReadOnly"/>
    /// indicating that the requested operation cannot run because the file system is configured
    /// as read-only.
    /// </summary>
    /// <param name="operation">The name of the operation that was rejected (e.g. <c>nameof(CreateFile)</c>).</param>
    /// <param name="innerException">Optional inner exception.</param>
    [DoesNotReturn]
    public static void ThrowReadOnly(
        string operation,
        [AllowNull] Exception? innerException = null)
    {
        var message = string.IsNullOrEmpty(operation)
            ? "The operation is not allowed; the file system is read-only."
            : $"The operation '{operation}' is not allowed; the file system is read-only.";
        throw new FileSystemException(
            FileSystemErrorCode.ReadOnly,
            message,
            innerException);
    }
}

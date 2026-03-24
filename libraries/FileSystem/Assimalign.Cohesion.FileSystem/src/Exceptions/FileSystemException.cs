using System;
using System.IO;
using System.Diagnostics.CodeAnalysis;

namespace Assimalign.Cohesion.FileSystem;

using Properties;

public class FileSystemException : SystemException
{
    public FileSystemException(string message) 
        : this(FileSystemErrorCode.Other, message)
    {
    }

    public FileSystemException(FileSystemErrorCode code, string message) 
        : base(message)
    {
        Code = code;
    }

    public FileSystemException(FileSystemErrorCode code, string message, Exception? innerException) 
        : base(message, innerException)
    {
        Code = code;
    }

    /// <summary>
    /// 
    /// </summary>
    public virtual FileSystemErrorCode Code { get; } = FileSystemErrorCode.Other;

    /// <summary>
    /// Throws a <see cref="FileSystemException"/> indicating that the directory does not exist.
    /// </summary>
    /// <param name="path"></param>
    /// <param name="innerException"></param>
    /// <exception cref="FileSystemException"></exception>
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
    /// <param name="path"></param>
    /// <param name="innerException"></param>
    /// <exception cref="FileSystemException"></exception>
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
    /// Throws a <see cref="FileSystemException"/> indicating that the path is too long.
    /// </summary>
    /// <param name="path"></param>
    /// <param name="innerException"></param>
    /// <exception cref="FileSystemException"></exception>
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
    /// <param name="path"></param>
    /// <param name="innerException"></param>
    /// <exception cref="FileSystemException"></exception>
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
    /// 
    /// </summary>
    /// <param name="path"></param>
    /// <param name="innerException"></param>
    /// <exception cref="FileSystemException"></exception>
    public static void ThrowPathInUse(
        [NotNull] FileSystemPath path,
        [AllowNull] Exception? innerException = null)
    {
        var message = string.Format("The specified path is in use by another process `{0}`.", path);
        throw new FileSystemException(
            FileSystemErrorCode.PathInUse,
            message,
            innerException);
    }

    /// <summary>
    /// Throws a <see cref="FileSystemException"/> indicating that the file or directory already exists.
    /// </summary>
    /// <param name="path"></param>
    /// <param name="innerException"></param>
    /// <exception cref="FileSystemException"></exception>
    [DoesNotReturn]
    public static void ThrowPathAlreadyExist(
        [NotNull] FileSystemPath path,
        [AllowNull] Exception? innerException = null)
    {
        var message = string.Format("The file or directory already exists `{0}`.", path);
        throw new FileSystemException(
            FileSystemErrorCode.Conflict,
            message,
            innerException);
    }

    /// <summary>
    /// 
    /// </summary>
    /// <param name="innerException"></param>
    /// <exception cref="FileSystemException"></exception>
    [DoesNotReturn]
    public static void ThrowNotEnoughSpace(
        [AllowNull] Exception? innerException = null)
    {
        throw new FileSystemException(
            FileSystemErrorCode.NotEnoughSpace,
            ErrorMessages.NotEnoughSpace,
            innerException);
    }
}

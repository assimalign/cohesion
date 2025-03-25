using System;
using System.Diagnostics.CodeAnalysis;
using System.IO;

namespace Assimalign.Cohesion.FileSystem;

public class FileSystemException : CohesionException
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


    [DoesNotReturn]
    public static FileSystemException ThrowNotFound(FileSystemPath path, Exception? innerException = null)
    {
        throw new FileSystemException(
            FileSystemErrorCode.NotFound,
            string.Format("The provided path does not exist `{0}`.", path),
            innerException);
    }
}

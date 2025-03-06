using System;
using System.IO;
using System.Diagnostics.CodeAnalysis;

namespace Assimalign.Cohesion.Internal;

using Assimalign.Cohesion.FileSystem;

internal static partial class ThrowHelper
{
    [DoesNotReturn]
    internal static void ThrowPathNotExistException(FileSystemPath path)
    {
        throw GetArgumentNullException(path);
    }

    [DoesNotReturn]
    internal static void ThrowFileSystemIsReadOnly()
    {
        throw new FileSystemException("The file system is readonly");
    }

    internal static FileSystemException GetPathNotFoundException(FileSystemPath path)
    {
        return new FileSystemException(string.Format("The provided path does not exist `{0}`.", path));
    }

    internal static FileSystemException GetUnhandledFileSystemException(Exception innerException)
    {
        return new FileSystemException("", innerException);
    }
}

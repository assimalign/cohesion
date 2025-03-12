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
        throw new FileSystemException(
            string.Format("The provided path does not exist `{0}`.", 
            path));
    }

    [DoesNotReturn]
    internal static void ThrowFileSystemIsReadOnly()
    {
        throw new FileSystemException("The file system is readonly");
    }

    [DoesNotReturn]
    internal static void ThrowFileSystemException(string message, Exception innerException)
    {
        throw new FileSystemException(message, innerException);
    }
}

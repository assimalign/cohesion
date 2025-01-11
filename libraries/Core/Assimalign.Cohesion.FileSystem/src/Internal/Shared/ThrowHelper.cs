using System;
using System.IO;
using System.Diagnostics.CodeAnalysis;

namespace Assimalign.Cohesion.Internal;

using Assimalign.Cohesion.FileSystem;

internal static partial class ThrowHelper
{
    [DoesNotReturn]
    internal static void ThrowFileNotExistException(FileSystemPath path)
    {
        throw new FileSystemException($"The given path does not exist: '{path}'.");
    }

    [DoesNotReturn]
    internal static void ThrowFileSystemIsReadOnly()
    {
        throw new FileSystemException("The file system is readonly");
    }
}

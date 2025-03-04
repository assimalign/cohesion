using Assimalign.Cohesion.Internal;
using System.Collections.Generic;
using System.Runtime.CompilerServices;

namespace Assimalign.Cohesion.FileSystem;

public static class FileSystemExtensions
{
    //public static Size GetUsedSpace(this IFileSystem fileSystem)
    //{
    //    var size = fileSystem.Size;
    //    var space = fileSystem.Space;

    //    return (size - space);
    //}



    /// <summary>
    /// 
    /// </summary>
    /// <param name="directory"></param>
    /// <param name="parent"></param>
    /// <returns></returns>
    public static bool HasParent(this IFileSystemDirectory directory, out IFileSystemDirectory parent)
    {
        return (parent = directory?.Parent!) is not null;
    }

    /// <summary>
    /// Recursively enumerates through the entire file system.
    /// </summary>
    /// <param name="fileSystem"></param>
    /// <returns></returns>
    public static IEnumerable<IFileSystemInfo> EnumerateFileSystemInfo(this IFileSystem fileSystem)
    {
        ThrowHelper.ThrowIfNull(fileSystem, nameof(fileSystem));

        foreach (var entry in fileSystem)
        {
            yield return entry;

            if (entry is IFileSystemDirectory directory)
            {
                foreach (var child in directory.EnumerateFileSystemInfo())
                {
                    yield return child;
                }
            }
        }
    }

    /// <summary>
    /// Recursively enumerates through the entire directory.
    /// </summary>
    /// <param name="diectory"></param>
    /// <returns></returns>
    public static IEnumerable<IFileSystemInfo> EnumerateFileSystemInfo(this IFileSystemDirectory diectory)
    {
        ThrowHelper.ThrowIfNull(diectory, nameof(diectory));

        foreach (var entry in diectory)
        {
            yield return entry;

            if (entry is IFileSystemDirectory directory1)
            {
                foreach (var child in directory1.EnumerateFileSystemInfo())
                {
                    yield return child;
                }
            }
        }
    }
}

using System.IO;
using System.Collections.Generic;
using System.Runtime.CompilerServices;

namespace Assimalign.Cohesion.FileSystem;

using Assimalign.Cohesion.Internal;

public static class FileSystemExtensions
{
    /// <summary>
    /// 
    /// </summary>
    /// <param name="path"></param>
    /// <param name="info"></param>
    /// <returns></returns>
    //public static bool TryGetFileSystemInfo(this IFileSystem fileSystem, FileSystemPath path, out IFileSystemInfo? info)
    //{
    //    ThrowHelper.ThrowIfNull(fileSystem);

    //    if (fileSystem.Exists(path))
    //    {

    //    }
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
    /// 
    /// </summary>
    /// <param name="directory"></param>
    /// <returns></returns>
    public static bool Exists(this IFileSystemDirectory directory, FileSystemPath path)
    {
        /*
            TODO: Not sure whether to include this API on IFileSystemDirectory. Will leave as extension method 
            and reevaluate later
         */

        var names = path.GetSegments();
        
        IFileSystemDirectory parent = ThrowHelper.ThrowIfNull(directory); ;

        for (int i = 0; i < names.Length || parent is null; i++)
        {
            var hasMore = i + 1 < names.Length;
            var name = names[i];

            if (parent is null) break;

            foreach (var item in parent)
            {
                if (item is IFileSystemFile file && !hasMore && file.Name.Equals(name))
                {
                    return true;
                }
                if (item is IFileSystemDirectory dir && dir.Name.Equals(name))
                {
                    if (hasMore)
                    {
                        parent = dir;
                        continue;
                    }

                    return true;
                }
            }
        }

        return false;
    }
}
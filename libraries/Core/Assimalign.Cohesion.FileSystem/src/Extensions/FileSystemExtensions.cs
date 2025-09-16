using System.IO;
using System.Collections.Generic;
using System.Runtime.CompilerServices;

namespace Assimalign.Cohesion.FileSystem;

using Assimalign.Cohesion.Internal;

public static class FileSystemExtensions
{
    extension(IFileSystemInfo info)
    {
        /// <summary>
        /// 
        /// </summary>
        /// <typeparam name="T"></typeparam>
        /// <param name="parent"></param>
        /// <returns></returns>
        public bool HasParent<T>(out T parent) where T : class, IFileSystemInfo
        {
            parent = default!;
            if (info.HasParent(out IFileSystemDirectory? p) && p is T type)
            {
                parent = type;
                return true;
            }
            return false;
        }

        /// <summary>
        /// 
        /// </summary>
        /// <param name="parent"></param>
        /// <returns></returns>
        public bool HasParent(out IFileSystemDirectory? parent)
        {
            ThrowHelper.ThrowIfNull(info);

            parent = info switch
            {
                IFileSystemDirectory directory when directory.Parent is not null => directory.Parent,
                IFileSystemFile file when file.Directory is not null => file.Directory,
                _ => null
            };

            return parent is not null;
        }
    }

    extension(IFileSystemDirectory directory)
    {
        /// <summary>
        /// 
        /// </summary>
        /// <returns></returns>
        public bool Exists(FileSystemPath path)
        {
            /*
                TODO: Not sure whether to include this API on IFileSystemDirectory. Will leave as extension method 
                and reevaluate later
             */

            FileSystemPath fullPath = directory.Path.Merge(path);

            return ThrowHelper.ThrowIfNull(directory).FileSystem.Exists(fullPath);
        }

        /// <summary>
        /// 
        /// </summary>
        /// <param name="path"></param>
        /// <returns></returns>

        public IFileSystemDirectory CreateSubdirectory(FileSystemPath path)
        {
            ThrowHelper.ThrowIfNull(directory);

            FileSystemPath newPath = directory.Path.Merge(path);

            return directory.FileSystem.CreateDirectory(newPath);
        }
    }
}
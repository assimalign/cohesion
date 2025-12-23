using System.IO;
using System.Collections.Generic;
using System.Runtime.CompilerServices;

namespace Assimalign.Cohesion.FileSystem;

using Assimalign.Cohesion.Internal;
using System;
using System.Diagnostics.CodeAnalysis;

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
        public bool HasParent<T>([NotNullWhen(true)] out T parent) where T : class, IFileSystemInfo
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
        public bool HasParent([NotNullWhen(true)] out IFileSystemDirectory? parent)
        {
            ArgumentNullException.ThrowIfNull(info);

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
            ArgumentNullException.ThrowIfNull(directory);
            /*
                TODO: Not sure whether to include this API on IFileSystemDirectory. Will leave as extension method 
                and reevaluate later
             */

            FileSystemPath fullPath = directory.Path.Merge(path);

            return directory.FileSystem.Exists(fullPath);
        }

        /// <summary>
        /// 
        /// </summary>
        /// <param name="path"></param>
        /// <returns></returns>

        public IFileSystemDirectory CreateSubdirectory(FileSystemPath path)
        {
            ArgumentNullException.ThrowIfNull(directory);

            FileSystemPath newPath = directory.Path.Merge(path);

            return directory.FileSystem.CreateDirectory(newPath);
        }
    }
}
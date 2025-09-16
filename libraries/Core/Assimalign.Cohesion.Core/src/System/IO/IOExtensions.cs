using System;
using System.Collections.Generic;
using System.Text;

namespace System.IO;

public static class IOExtensions
{
    extension(FileSystemPath path)
    {
        /// <summary>
        /// Returns the end of the path as a <see cref="FileName"/>, if any.
        /// </summary>
        /// <param name="path"></param>
        /// <returns></returns>
        public FileName? GetFileName()
        {
            return Path.GetFileName(path);
        }

        /// <summary>
        /// Returns the end of the path as a <see cref="DirectoryName"/>, if any.
        /// </summary>
        /// <returns></returns>
        public DirectoryName? GetDirectoryName()
        {
            return Path.GetDirectoryName(path.AsSpan())!;
        }

        /// <summary>
        /// Returns all the directories in the path as an array of <see cref="DirectoryName"/>.
        /// </summary>
        /// <returns></returns>
        public DirectoryName[] GetDirectories()
        {
            var segments = path.GetSegments();
            var directories = new DirectoryName[segments.Length];

            for (int i = 0; i < segments.Length; i++)
            {
                directories[i] = segments[i];
            }

            return directories;
        }
    }
}

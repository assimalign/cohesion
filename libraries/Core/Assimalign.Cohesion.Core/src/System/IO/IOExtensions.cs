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

        public DirectoryName? GetLastDirectoryName()
        {
            return path.GetSegments()[^1];
        }

        /// <summary>
        /// Returns all the directories in the path as an array of <see cref="DirectoryName"/>.
        /// </summary>
        /// <returns></returns>
        public DirectoryName[] GetDirectoryNames()
        {

            int skip = 0;
            var segments = path.GetSegments();
            var directories = new DirectoryName[segments.Length];

            for (int i = 0; i < segments.Length; i++)
            {
                string segment = segments[i];

                // Skip relative paths
                if (segment.Equals(".."))
                {
                    skip++;
                }
                else
                {
                    directories[i - skip] = segment;
                }
            }

            Array.Resize(ref directories, segments.Length - skip);

            return directories;
        }
    }
}

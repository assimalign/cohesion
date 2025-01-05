
using System;
using System.IO;
using System.Linq;
using System.Collections.Generic;

namespace Assimalign.Extensions.FileSystemGlobbing;

/// <summary>
/// Avoids using disk for uses like Pattern Matching.
/// </summary>
public class InMemoryFileDirectoryInfo : IFileComponentContainer
{
    private static readonly char[] DirectorySeparators = new[] { Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar };
    private readonly IEnumerable<string> _files;

    /// <summary>
    /// Creates a new InMemoryDirectoryInfo with the root directory and files given.
    /// </summary>
    /// <param name="rootDir">The root directory that this FileSystem will use.</param>
    /// <param name="files">Collection of file names. If relative paths <paramref name="rootDir"/> will be prepended to the paths.</param>
    public InMemoryFileDirectoryInfo(string rootDir, IEnumerable<string> files) : this(rootDir, files, false)
    {
    }

    private InMemoryFileDirectoryInfo(string rootDir, IEnumerable<string> files, bool normalized)
    {
        if (string.IsNullOrEmpty(rootDir))
        {
            throw new ArgumentNullException(nameof(rootDir));
        }

        if (files == null)
        {
            files = new List<string>();
        }

        Name = Path.GetFileName(rootDir);
        if (normalized)
        {
            _files = files;
            FullName = rootDir;
        }
        else
        {
            var fileList = new List<string>(files.Count());

            // normalize
            foreach (string file in files)
            {
                fileList.Add(Path.GetFullPath(file.Replace(Path.AltDirectorySeparatorChar, Path.DirectorySeparatorChar)));
            }

            _files = fileList;

            FullName = Path.GetFullPath(rootDir.Replace(Path.AltDirectorySeparatorChar, Path.DirectorySeparatorChar));
        }
    }

    /// <inheritdoc />
    public string FullName { get; }

    /// <inheritdoc />
    public string Name { get; }

    /// <inheritdoc />
    public InMemoryFileDirectoryInfo ParentDirectory =>
        new InMemoryFileDirectoryInfo(Path.GetDirectoryName(FullName), _files, true);

    IFileComponentContainer IFileComponent.ParentComponent => this.ParentDirectory;

    /// <inheritdoc />
    public IEnumerable<IFileComponent> EnumerateFileComponents()
    {
        var dict = new Dictionary<string, List<string>>();

        foreach (string file in _files)
        {
            if (!IsRootDirectory(FullName, file))
            {
                continue;
            }

            int endPath = file.Length;
            int beginSegment = FullName.Length + 1;
            int endSegment = file.IndexOfAny(DirectorySeparators, beginSegment, endPath - beginSegment);

            if (endSegment == -1)
            {
                yield return new InMemoryFileInfo(file, this);
            }
            else
            {
                string name = file.Substring(0, endSegment);
                List<string> list;
                if (!dict.TryGetValue(name, out list))
                {
                    dict[name] = new List<string> { file };
                }
                else
                {
                    list.Add(file);
                }
            }
        }

        foreach (KeyValuePair<string, List<string>> item in dict)
        {
            yield return new InMemoryFileDirectoryInfo(item.Key, item.Value, true);
        }
    }

    private bool IsRootDirectory(string rootDir, string filePath)
    {
        int rootDirLength = rootDir.Length;

        return filePath.StartsWith(rootDir, StringComparison.Ordinal) &&
            (rootDir[rootDirLength - 1] == Path.DirectorySeparatorChar ||
            filePath.IndexOf(Path.DirectorySeparatorChar, rootDirLength) == rootDirLength);
    }

    /// <inheritdoc />
    public InMemoryFileDirectoryInfo GetDirectory(string path)
    {
        if (string.Equals(path, "..", StringComparison.Ordinal))
        {
            return new InMemoryFileDirectoryInfo(Path.Combine(FullName, path), _files, true);
        }
        else
        {
            string normPath = Path.GetFullPath(path.Replace(Path.AltDirectorySeparatorChar, Path.DirectorySeparatorChar));
            return new InMemoryFileDirectoryInfo(normPath, _files, true);
        }
    }

    IFileComponentContainer IFileComponentContainer.GetContainer(string path) => this.GetDirectory(path);

    /// <summary>
    /// Returns an instance of <see cref="FileInfo"/> that matches the <paramref name="path"/> given.
    /// </summary>
    /// <param name="path">The filename.</param>
    /// <returns>Instance of <see cref="FileInfo"/> if the file exists, null otherwise.</returns>
    public InMemoryFileInfo GetFile(string path)
    {
        var normPath = Path.GetFullPath(path.Replace(Path.AltDirectorySeparatorChar, Path.DirectorySeparatorChar));
        
        foreach (string file in _files)
        {
            if (string.Equals(file, normPath))
            {
                return new InMemoryFileInfo(file, this);
            }
        }

        return null;
    }

    IFileComponent IFileComponentContainer.GetComponent(string path) => this.GetFile(path);
}

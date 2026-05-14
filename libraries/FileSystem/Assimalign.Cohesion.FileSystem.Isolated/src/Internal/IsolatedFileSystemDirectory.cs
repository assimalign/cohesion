using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.IO.IsolatedStorage;
using System.Linq;

namespace Assimalign.Cohesion.FileSystem.Internal;

/// <summary>
/// <see cref="IFileSystemDirectory"/> implementation backed by a relative path inside an
/// <see cref="IsolatedStorageFile"/>. All store interaction goes through
/// <see cref="IsolatedPathHelper"/> so the public <see cref="FileSystemPath"/> surface stays
/// '/' separated regardless of the host OS.
/// </summary>
[DebuggerDisplay("[D] - {Path}")]
internal sealed class IsolatedFileSystemDirectory : IsolatedFileSystemInfo, IFileSystemDirectory
{
    public IsolatedFileSystemDirectory(
        IsolatedFileSystem fileSystem,
        IsolatedStorageFile storage,
        FileSystemPath path)
        : base(fileSystem, storage, path)
    {
    }

    /// <inheritdoc />
    public DirectoryName Name
    {
        get
        {
            // Root directory has no segment-level name; surface "/" so callers see a stable value.
            string[] segments = Path.GetSegments();
            return segments.Length == 0 ? (DirectoryName)"/" : (DirectoryName)segments[^1];
        }
    }

    /// <inheritdoc />
    public IFileSystemDirectory? Parent
    {
        get
        {
            string text = Path.ToString();
            if (text == IsolatedPathHelper.Root || string.IsNullOrEmpty(text))
            {
                return null;
            }

            int lastSep = text.LastIndexOf('/');
            FileSystemPath parentPath = lastSep <= 0
                ? IsolatedPathHelper.Root
                : text.Substring(0, lastSep);

            return new IsolatedFileSystemDirectory(FileSystem, Storage, parentPath);
        }
    }

    /// <inheritdoc />
    public IFileSystemDirectory CreateDirectory(DirectoryName name)
        => FileSystem.CreateDirectory(Path.Join(name.ToString()));

    /// <inheritdoc />
    public IFileSystemFile CreateFile(FileName name)
        => FileSystem.CreateFile(Path.Join(name.ToString()));

    /// <inheritdoc />
    public void DeleteDirectory(DirectoryName name)
        => FileSystem.DeleteDirectory(Path.Join(name.ToString()));

    /// <inheritdoc />
    public void DeleteFile(FileName name)
        => FileSystem.DeleteFile(Path.Join(name.ToString()));

    /// <inheritdoc />
    public IFileSystemDirectory GetDirectory(DirectoryName name)
        => FileSystem.GetDirectory(Path.Join(name.ToString()));

    /// <inheritdoc />
    public IFileSystemFile GetFile(FileName name)
        => FileSystem.GetFile(Path.Join(name.ToString()));

    /// <inheritdoc />
    public IEnumerable<IFileSystemDirectory> GetDirectories()
    {
        string pattern = IsolatedPathHelper.ChildSearchPattern(Path);
        string[] names;

        try
        {
            names = Storage.GetDirectoryNames(pattern);
        }
        catch (IsolatedStorageException ex)
        {
            FileSystemException.ThrowPathNotFound(Path, ex);
            return Array.Empty<IFileSystemDirectory>();
        }

        var directories = new List<IFileSystemDirectory>(names.Length);
        foreach (var name in names)
        {
            directories.Add(new IsolatedFileSystemDirectory(
                FileSystem,
                Storage,
                Path.Join(name)));
        }

        return directories;
    }

    /// <inheritdoc />
    public IEnumerable<IFileSystemFile> GetFiles()
    {
        string pattern = IsolatedPathHelper.ChildSearchPattern(Path);
        string[] names;

        try
        {
            names = Storage.GetFileNames(pattern);
        }
        catch (IsolatedStorageException ex)
        {
            FileSystemException.ThrowPathNotFound(Path, ex);
            return Array.Empty<IFileSystemFile>();
        }

        var files = new List<IFileSystemFile>(names.Length);
        foreach (var name in names)
        {
            files.Add(new IsolatedFileSystemFile(
                FileSystem,
                Storage,
                Path.Join(name)));
        }

        return files;
    }

    /// <inheritdoc />
    public IEnumerable<IFileSystemInfo> EnumerateFileSystem(FileSystemEnumerationOptions? options = null)
    {
        options ??= new FileSystemEnumerationOptions
        {
            Recurse = false
        };

        return EnumerateCore(this, options);
    }

    /// <inheritdoc />
    /// <remarks>
    /// Returns a polling token scoped to this directory subtree. The cadence is configured via
    /// <see cref="IsolatedFileSystemOptions.WatchPollInterval"/>; see
    /// <see cref="IsolatedFileSystem.Watch(Glob?)"/> for details.
    /// </remarks>
    public IFileSystemEventToken Watch(Glob? pattern)
        => FileSystem.CreateWatchToken(Path, pattern);

    /// <inheritdoc />
    public IEnumerator<IFileSystemInfo> GetEnumerator()
        => EnumerateFileSystem(new FileSystemEnumerationOptions { Recurse = true }).GetEnumerator();

    IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();

    private static IEnumerable<IFileSystemInfo> EnumerateCore(
        IsolatedFileSystemDirectory directory,
        FileSystemEnumerationOptions options)
    {
        foreach (var dir in directory.GetDirectories())
        {
            yield return dir;

            if (options.Recurse && dir is IsolatedFileSystemDirectory child)
            {
                foreach (var nested in EnumerateCore(child, options))
                {
                    yield return nested;
                }
            }
        }

        foreach (var file in directory.GetFiles())
        {
            yield return file;
        }
    }
}

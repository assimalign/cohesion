using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;

namespace Assimalign.Cohesion.FileSystem.Internal;

/// <summary>
/// A synthetic <see cref="IFileSystemDirectory"/> exposed by <see cref="AggregateFileSystem"/>
/// for paths that are intermediate segments leading to a mount, but which are not themselves
/// served by any mounted provider. Example: when only "/data/cache" is mounted, "/data" is
/// synthetic — it exists for traversal but cannot host files or directories of its own.
/// </summary>
/// <remarks>
/// Synthetic directories are read-only. Every mutating operation throws
/// <see cref="FileSystemException"/> with code
/// <see cref="FileSystemErrorCode.ReadOnly"/> to make the intent unambiguous.
/// </remarks>
[DebuggerDisplay("[Synthetic] - {Path}")]
internal sealed class AggregateSyntheticDirectory : IFileSystemDirectory
{
    private readonly AggregateFileSystem _aggregate;
    private readonly FileSystemPath _path;

    public AggregateSyntheticDirectory(AggregateFileSystem aggregate, FileSystemPath path)
    {
        _aggregate = aggregate;
        _path = path;
    }

    /// <inheritdoc />
    public FileSystemPath Path => _path;

    /// <inheritdoc />
    public DateTime CreatedOn => DateTime.MinValue;

    /// <inheritdoc />
    public DateTime UpdatedOn => DateTime.MinValue;

    /// <inheritdoc />
    public DateTime AccessedOn => DateTime.MinValue;

    /// <inheritdoc />
    public FileAttributes Attributes => FileAttributes.Directory;

    /// <inheritdoc />
    public IFileSystem FileSystem => _aggregate;

    /// <inheritdoc />
    public DirectoryName Name
    {
        get
        {
            string text = _path.ToString();
            if (text == "/" || string.IsNullOrEmpty(text))
            {
                return (DirectoryName)"/";
            }
            int lastSep = text.LastIndexOf('/');
            return (DirectoryName)(lastSep < 0 ? text : text.Substring(lastSep + 1));
        }
    }

    /// <inheritdoc />
    public IFileSystemDirectory? Parent
    {
        get
        {
            string text = _path.ToString();
            if (text == "/" || string.IsNullOrEmpty(text))
            {
                return null;
            }
            int lastSep = text.LastIndexOf('/');
            FileSystemPath parentPath = lastSep <= 0 ? "/" : text.Substring(0, lastSep);
            return _aggregate.ResolveDirectory(parentPath);
        }
    }

    /// <inheritdoc />
    public void SetAttributes(FileAttributes attributes) => ThrowReadOnly(nameof(SetAttributes));

    /// <inheritdoc />
    public IFileSystemDirectory CreateDirectory(DirectoryName name)
    {
        ThrowReadOnly(nameof(CreateDirectory));
        return default!;
    }

    /// <inheritdoc />
    public IFileSystemFile CreateFile(FileName name)
    {
        ThrowReadOnly(nameof(CreateFile));
        return default!;
    }

    /// <inheritdoc />
    public void DeleteDirectory(DirectoryName name) => ThrowReadOnly(nameof(DeleteDirectory));

    /// <inheritdoc />
    public void DeleteFile(FileName name) => ThrowReadOnly(nameof(DeleteFile));

    /// <inheritdoc />
    public IFileSystemDirectory GetDirectory(DirectoryName name)
    {
        FileSystemPath childPath = Join(_path, name.ToString());
        var result = _aggregate.TryResolveDirectory(childPath);
        if (result is null)
        {
            FileSystemException.ThrowDirectoryNotFound(childPath);
        }
        return result!;
    }

    /// <inheritdoc />
    public IFileSystemFile GetFile(FileName name)
    {
        FileSystemPath childPath = Join(_path, name.ToString());
        FileSystemException.ThrowFileNotFound(childPath);
        return default!;
    }

    /// <inheritdoc />
    public IEnumerable<IFileSystemDirectory> GetDirectories()
    {
        // Children are the synthetic prefixes one level below us PLUS any mount whose root is
        // exactly one level below us.
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var child in _aggregate.GetSyntheticChildren(_path))
        {
            FileSystemPath childPath = Join(_path, child);
            seen.Add(child);
            var resolved = _aggregate.TryResolveDirectory(childPath);
            if (resolved is not null)
            {
                yield return resolved;
            }
        }
    }

    /// <inheritdoc />
    public IEnumerable<IFileSystemFile> GetFiles() => Array.Empty<IFileSystemFile>();

    /// <inheritdoc />
    public IEnumerable<IFileSystemInfo> EnumerateFileSystem(FileSystemEnumerationOptions? options = null)
    {
        options ??= new FileSystemEnumerationOptions { Recurse = false };

        foreach (var dir in GetDirectories())
        {
            yield return dir;

            if (options.Recurse)
            {
                foreach (var nested in dir.EnumerateFileSystem(options))
                {
                    yield return nested;
                }
            }
        }
    }

    /// <inheritdoc />
    public IFileSystemEventToken Watch(Glob? pattern) => _aggregate.Watch(pattern);

    /// <inheritdoc />
    public IEnumerator<IFileSystemInfo> GetEnumerator()
        => EnumerateFileSystem(new FileSystemEnumerationOptions { Recurse = true }).GetEnumerator();

    IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();

    private static FileSystemPath Join(FileSystemPath parent, string child)
    {
        string text = parent.ToString();
        if (text == "/" || string.IsNullOrEmpty(text))
        {
            return "/" + child;
        }
        return text + "/" + child;
    }

    private static void ThrowReadOnly(string op)
        => FileSystemException.ThrowReadOnly(op);
}

using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;

namespace Assimalign.Cohesion.FileSystem.Internal;

/// <summary>
/// Wraps an <see cref="IFileSystemDirectory"/> from a mounted provider so the path surface stays
/// in aggregate-space. Child enumerations are re-wrapped, and create/delete calls are forwarded
/// through the aggregate so the routing layer can validate mounts.
/// </summary>
[DebuggerDisplay("[D] - {Path}")]
internal sealed class AggregateFileSystemDirectory : IFileSystemDirectory
{
    private readonly AggregateFileSystem _aggregate;
    private readonly AggregateMount _mount;
    private readonly IFileSystemDirectory _inner;
    private readonly FileSystemPath _aggregatePath;

    public AggregateFileSystemDirectory(
        AggregateFileSystem aggregate,
        AggregateMount mount,
        IFileSystemDirectory inner,
        FileSystemPath aggregatePath)
    {
        _aggregate = aggregate;
        _mount = mount;
        _inner = inner;
        _aggregatePath = aggregatePath;
    }

    /// <inheritdoc />
    public FileSystemPath Path => _aggregatePath;

    /// <inheritdoc />
    public DateTime CreatedOn => _inner.CreatedOn;

    /// <inheritdoc />
    public DateTime UpdatedOn => _inner.UpdatedOn;

    /// <inheritdoc />
    public DateTime AccessedOn => _inner.AccessedOn;

    /// <inheritdoc />
    public FileAttributes Attributes => _inner.Attributes;

    /// <inheritdoc />
    public IFileSystem FileSystem => _aggregate;

    /// <inheritdoc />
    public DirectoryName Name => _inner.Name;

    /// <inheritdoc />
    public IFileSystemDirectory? Parent
    {
        get
        {
            // If we're at the mount root, the parent crosses the mount boundary and lives in the
            // aggregate's synthetic space. Defer to the aggregate so it can resolve correctly.
            if (_aggregatePath.Equals(_mount.MountPath))
            {
                return _aggregate.GetParentForMountRoot(_mount);
            }

            var innerParent = _inner.Parent;
            if (innerParent is null)
            {
                return null;
            }

            FileSystemPath aggregateParent = _mount.ToAggregatePath(innerParent.Path);
            return new AggregateFileSystemDirectory(_aggregate, _mount, innerParent, aggregateParent);
        }
    }

    /// <inheritdoc />
    public void SetAttributes(FileAttributes attributes) => _inner.SetAttributes(attributes);

    /// <inheritdoc />
    public IFileSystemDirectory CreateDirectory(DirectoryName name)
        => (IFileSystemDirectory)_aggregate.GetInfo(_aggregate.CreateDirectoryUnderMount(this, name).Path);

    /// <inheritdoc />
    public IFileSystemFile CreateFile(FileName name)
        => (IFileSystemFile)_aggregate.GetInfo(_aggregate.CreateFileUnderMount(this, name).Path);

    /// <inheritdoc />
    public void DeleteDirectory(DirectoryName name)
    {
        var child = _inner.GetDirectory(name);
        _inner.DeleteDirectory(name);
    }

    /// <inheritdoc />
    public void DeleteFile(FileName name) => _inner.DeleteFile(name);

    /// <inheritdoc />
    public IFileSystemDirectory GetDirectory(DirectoryName name)
    {
        var child = _inner.GetDirectory(name);
        FileSystemPath aggregateChild = _mount.ToAggregatePath(child.Path);
        return new AggregateFileSystemDirectory(_aggregate, _mount, child, aggregateChild);
    }

    /// <inheritdoc />
    public IFileSystemFile GetFile(FileName name)
    {
        var child = _inner.GetFile(name);
        FileSystemPath aggregateChild = _mount.ToAggregatePath(child.Path);
        return new AggregateFileSystemFile(_aggregate, _mount, child, aggregateChild);
    }

    /// <inheritdoc />
    public IEnumerable<IFileSystemDirectory> GetDirectories()
    {
        foreach (var child in _inner.GetDirectories())
        {
            FileSystemPath aggregateChild = _mount.ToAggregatePath(child.Path);
            yield return new AggregateFileSystemDirectory(_aggregate, _mount, child, aggregateChild);
        }
    }

    /// <inheritdoc />
    public IEnumerable<IFileSystemFile> GetFiles()
    {
        foreach (var child in _inner.GetFiles())
        {
            FileSystemPath aggregateChild = _mount.ToAggregatePath(child.Path);
            yield return new AggregateFileSystemFile(_aggregate, _mount, child, aggregateChild);
        }
    }

    /// <inheritdoc />
    public IEnumerable<IFileSystemInfo> EnumerateFileSystem(FileSystemEnumerationOptions? options = null)
    {
        foreach (var info in _inner.EnumerateFileSystem(options))
        {
            FileSystemPath aggregateChildPath = _mount.ToAggregatePath(info.Path);
            yield return info switch
            {
                IFileSystemFile file => new AggregateFileSystemFile(_aggregate, _mount, file, aggregateChildPath),
                IFileSystemDirectory dir => new AggregateFileSystemDirectory(_aggregate, _mount, dir, aggregateChildPath),
                _ => info,
            };
        }
    }

    /// <inheritdoc />
    public IFileSystemEventToken Watch(Glob? pattern) => _inner.Watch(pattern);

    /// <inheritdoc />
    public IEnumerator<IFileSystemInfo> GetEnumerator()
        => EnumerateFileSystem(new FileSystemEnumerationOptions { Recurse = true }).GetEnumerator();

    IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
}

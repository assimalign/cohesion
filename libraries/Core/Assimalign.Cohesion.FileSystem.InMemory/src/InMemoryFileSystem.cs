using System;
using System.Collections;
using System.IO;
using System.Linq;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading.Tasks;

namespace Assimalign.Cohesion.FileSystem;

using Assimalign.Cohesion.Internal;
using Assimalign.Cohesion.FileSystem.Internal;

public class InMemoryFileSystem : IFileSystem
{
    // The locking strategy is based on https://www.kernel.org/doc/Documentation/filesystems/directory-locking

    private readonly InMemoryFileSystemDirectory _root;
    private Size _size;
    private Size _spaceUsed;

    public InMemoryFileSystem(InMemoryFileSystemOptions options)
    {
        ThrowHelper.ThrowIfNull(options, nameof(options));

        _size = options.Size;
        _root = new InMemoryFileSystemDirectory(options.RootName)
        {
            FileSystem = this,
            Comparer = GetComparer(options)
        };
    }

    public Size Size => _size;
    public Size SpaceAvailable => _size - _spaceUsed;
    public Size SpaceUsed => _spaceUsed;
    public IFileSystemDirectory RootDirectory => _root;

    #region Methods

    public void CopyFile(FileSystemPath source, FileSystemPath destination)
    {
        RootDirectory.CopyFile(source, destination);
    }
    public IFileSystemDirectory CreateDirectory(FileSystemPath path)
    {
        return RootDirectory.CreateDirectory(path);
    }
    public IFileSystemFile CreateFile(FileSystemPath path)
    {
        return RootDirectory.CreateFile(path);
    }
    public void DeleteDirectory(FileSystemPath path)
    {
        RootDirectory.DeleteDirectory(path);
    }
    public void DeleteFile(FileSystemPath path)
    {
        RootDirectory.DeleteFile(path);
    }
    public bool Exists(FileSystemPath path)
    {
        return RootDirectory.Exist(path);
    }
    public IEnumerable<IFileSystemDirectory> EnumerateDirectories()
    {
        return RootDirectory.GetDirectories();
    }
    public IFileSystemDirectory GetDirectory(FileSystemPath path)
    {
        return RootDirectory.GetDirectory(path);
    }
    public IEnumerator<IFileSystemInfo> GetEnumerator()
    {
        return RootDirectory.GetEnumerator();
    }
    public IFileSystemFile GetFile(FileSystemPath path)
    {
        return RootDirectory.GetFile(path);
    }
    public IEnumerable<IFileSystemFile> EnumerateFiles()
    {
        return RootDirectory.GetFiles();
    }
    public void Move(FileSystemPath source, FileSystemPath destination)
    {
       // RootDirectory.Move(source, destination);
    }
    public IFileSystemChangeToken Watch(Glob pattern)
    {
        return RootDirectory.Watch(pattern);
    }
    IEnumerator IEnumerable.GetEnumerator()
    {
        return GetEnumerator();
    }
    public void Dispose()
    {
        _root.Dispose();
    }
    public ValueTask DisposeAsync()
    {
        return ValueTask.CompletedTask;
    }

    internal void IncrementSpaceUsed(Size value)
    {
        _spaceUsed =+ value;
    }

    internal void DecrementSpaceUsed(Size value)
    {
        _spaceUsed =- value;
    }

    private StringComparer GetComparer(InMemoryFileSystemOptions options)
    {
        var comparer = StringComparer.Ordinal;

        if (options.CultureInfo is not null)
        {
            comparer = StringComparer.Create(options.CultureInfo, options.IgnoreCase);
        }
        else if (options.IgnoreCase)
        {
            comparer = StringComparer.OrdinalIgnoreCase;
        }

        return comparer;
    }

    #endregion
}

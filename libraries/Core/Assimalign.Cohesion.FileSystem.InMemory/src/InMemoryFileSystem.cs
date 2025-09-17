using System;
using System.IO;
using System.Linq;
using System.Collections;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace Assimalign.Cohesion.FileSystem;

using Assimalign.Cohesion.Internal;
using Assimalign.Cohesion.FileSystem.Internal;

public sealed partial class InMemoryFileSystem : IFileSystem
{
    // The locking strategy is based on https://www.kernel.org/doc/Documentation/filesystems/directory-locking

    private readonly Lock _lock = new Lock();
    private readonly InMemoryFileSystemDirectory _root;
    private readonly string _name;
    private readonly bool _isReadOnly;
    private Size _size;
    private Size _spaceUsed;

    /// <summary>
    /// 
    /// </summary>
    /// <param name="options"></param>
    public InMemoryFileSystem(InMemoryFileSystemOptions options)
    {
        ThrowHelper.ThrowIfNull(options, nameof(options));

        _isReadOnly = options.IsReadOnly;
        _name = options.Name ?? nameof(InMemoryFileSystem);
        _size = options.Size;
        _root = new InMemoryFileSystemDirectory(options.RootName, this)
        {
            Comparer = FileSystemPathComparer.Create(options.Comparison),
            IgnoreAttributes = options.IgnoreAttributes,
        };
    }

    public string Name => _name;
    public bool IsReadOnly => _isReadOnly;
    public Size Size => _size;
    public Size SpaceAvailable => _size - _spaceUsed;
    public Size SpaceUsed => _spaceUsed;
    public IFileSystemDirectory RootDirectory => _root;
    public void CopyFile(FileSystemPath source, FileSystemPath destination)
    {

    }
    public IFileSystemDirectory CreateDirectory(FileSystemPath path)
    {
        FileSystemPath fullPath = RootDirectory.Path.Merge(path);
        DirectoryName[] directories = fullPath.GetDirectories();
        
        InMemoryFileSystemDirectory directory = _root!;

        for (int i = 0; i < directories.Length; i++)
        {
            DirectoryName name = directories[i];

            if (directory.TryGetDirectory(name, out var existing))
            {
                // Check if last
                if ((i + 1) == directories.Length)
                {
                    ThrowHelper.ThrowFileOrDirectoryAlreadyExists(fullPath);
                }

                directory = existing;
                continue;
            }

            directory = (InMemoryFileSystemDirectory)directory.CreateDirectory(name);
        }

        return directory!;
    }
    public IFileSystemFile CreateFile(FileSystemPath path)
    {
        throw new NotImplementedException();
    }
    public void DeleteDirectory(FileSystemPath path)
    {
        throw new NotImplementedException();
    }
    public void DeleteFile(FileSystemPath path)
    {
        throw new NotImplementedException();
    }
    public bool Exists(FileSystemPath path)
    {
        throw new NotImplementedException();
    }
    public IEnumerable<IFileSystemDirectory> EnumerateDirectories()
    {
        throw new NotImplementedException();
    }
    public IFileSystemDirectory GetDirectory(FileSystemPath path)
    {
        throw new NotImplementedException();
    }
    public IEnumerator<IFileSystemInfo> GetEnumerator()
    {
        throw new NotImplementedException();
    }
    public IFileSystemFile GetFile(FileSystemPath path)
    {
        throw new NotImplementedException();
    }
    public void Move(FileSystemPath source, FileSystemPath destination)
    {
        // RootDirectory.Move(source, destination);
    }
    public IFileSystemChangeToken Watch(Glob? pattern)
    {
        throw new NotImplementedException();
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

    public IEnumerable<IFileSystemInfo> EnumerateFileSystem(FileSystemEnumerationOptions? options = null)
    {
        throw new NotImplementedException();
    }

    public IFileSystemInfo GetInfo(FileSystemPath path)
    {
        throw new NotImplementedException();
    }

    internal void IncrementSpaceUsed(Size value)
    {
        if ((_spaceUsed + value) > SpaceAvailable)
        {
            throw new IOException("There is not enough space in the file system to complete this operation.");
        }
        lock (_lock)
        {
            _spaceUsed += value;
        }
    }
}

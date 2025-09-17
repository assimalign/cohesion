using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;

namespace Assimalign.Cohesion.FileSystem.Internal;

using Assimalign.Cohesion.Internal;

[DebuggerDisplay("[D] - {Path}")]
internal class InMemoryFileSystemDirectory : InMemoryFileSystemInfo, IFileSystemDirectory
{
    private readonly Dictionary<FileSystemPath, InMemoryFileSystemInfo> _children;
    private readonly Dictionary<FileSystemPath, InMemoryFileSystemInfo>.AlternateLookup<ReadOnlySpan<char>> _lookup;

    private InMemoryFileSystemDirectory? _parent;
    private DirectoryName _name;

    public InMemoryFileSystemDirectory(DirectoryName name, InMemoryFileSystem fileSystem)
        : base(fileSystem)
    {
        _name = name;
        _children = new Dictionary<FileSystemPath, InMemoryFileSystemInfo>(Comparer);
        _lookup = _children.GetAlternateLookup<ReadOnlySpan<char>>();
    }

    public InMemoryFileSystemDirectory(DirectoryName name, InMemoryFileSystemDirectory parent, InMemoryFileSystem fileSystem)
        : this(name, fileSystem)
    {
        _parent = parent;
    }


    public int Count => _children.Count;
    public DirectoryName Name => _name;
    public InMemoryFileSystemDirectory? Parent => _parent;
    IFileSystemDirectory? IFileSystemDirectory.Parent => Parent;

    public bool TryGetDirectory(in DirectoryName name, out InMemoryFileSystemDirectory directory)
    {
        directory = default!;

        if (_lookup.TryGetValue(FormatPath(name), out var info) && info is InMemoryFileSystemDirectory dir)
        {
            directory = dir;
            return true;
        }

        return false;
    }

    public IFileSystemChangeToken Watch(Glob? glob)
    {
        return new InMemoryFileSystemChangeToken(
            this,
            glob ?? Glob.Parse(Path));
    }

    public IFileSystemDirectory CreateDirectory(DirectoryName name)
    {
        CheckIfReadOnly(nameof(CreateDirectory));

        Lock(LockPolicy.Read);

        InMemoryFileSystemDirectory? directory = default;

        try
        {
            FileSystemPath path = FormatPath(name);

            // Check if directory already or file exists
            if (_lookup.ContainsKey(path))
            {
                ThrowHelper.ThrowFileOrDirectoryAlreadyExists(
                    path,
                    new IOException($"Cannot create directory `{path}` on an existing file or directory"));
            }

            // Set new directory
            _children[path] = directory = new InMemoryFileSystemDirectory(name, this, FileSystem);

            return directory;
        }
        finally
        {
            if (directory is not null)
            {
                // Dispatch event
                Dispatcher.RaiseEvent(new FileSystemEventArgs(
                    WatcherChangeTypes.Created,
                    directory.Path,
                    directory.Name));
            }

            Unlock();
        }
    }

    public IFileSystemFile CreateFile(FileName name)
    {
        CheckIfReadOnly(nameof(CreateFile));

        // Place a exclusive lock on the directory
        Lock(LockPolicy.Exclusive);

        InMemoryFileSystemFile? file = default!;

        try
        {
            FileSystemPath path = FormatPath(name);

            // Check if directory already or file exists
            if (_lookup.ContainsKey(path))
            {
                ThrowHelper.ThrowFileOrDirectoryAlreadyExists(
                    path,
                    new IOException($"Cannot create directory `{path}` on an existing file or directory"));
            }

            // Set new directory
            _children[path] = file = new InMemoryFileSystemFile(name, this, FileSystem);

            return file!;
        }
        finally
        {
            if (file is not null)
            {
                // Dispatch event
                Dispatcher.RaiseEvent(new FileSystemEventArgs(
                    WatcherChangeTypes.Created,
                    file.Path,
                    file.Name));
            }

            Unlock();
        }
    }

    public void DeleteDirectory(DirectoryName name)
    {
        CheckIfReadOnly(nameof(DeleteDirectory));

        // Place a exclusive lock on the directory, and recurse into children
        // If another process has a lock on any child, this will wait until the lock is released or throw an unauthorized access exception
        Lock(LockPolicy.Exclusive, recurse: true);

        InMemoryFileSystemDirectory? directory = default!;

        try
        {
            FileSystemPath path = FormatPath(name);

            if (!_lookup.TryGetValue(path, out InMemoryFileSystemInfo? info))
            {
                ThrowHelper.ThrowPathNotFound(path);
            }
            if (info is not InMemoryFileSystemDirectory)
            {
                ThrowHelper.ThrowPathNotFound(path);
            }
            else
            {
                if (!_lookup.Remove(path))
                {
                    ThrowHelper.ThrowAccessNotAllowed(path);
                }

                directory = (InMemoryFileSystemDirectory)info;
            }

        }
        finally
        {
            if (directory is not null)
            {
                // Dispatch event
                Dispatcher.RaiseEvent(new FileSystemEventArgs(
                    WatcherChangeTypes.Deleted,
                    directory.Path,
                    directory.Name));
            }

            Unlock(recurse: true);
        }
    }

    public void DeleteFile(FileName name)
    {
        CheckIfReadOnly(nameof(DeleteFile));

        // Place a exclusive lock on the directory, and recurse into children
        // If another process has a lock on any child, this will wait until the lock is released or throw an unauthorized access exception
        Lock(LockPolicy.Exclusive);

        InMemoryFileSystemFile? file = default!;

        try
        {
            FileSystemPath path = FormatPath(name);

            if (!_lookup.TryGetValue(path, out InMemoryFileSystemInfo? info))
            {
                ThrowHelper.ThrowPathNotFound(path);
            }

            if (info is not InMemoryFileSystemFile)
            {
                ThrowHelper.ThrowPathNotFound(path);
            }

            else
            {
                if (!_lookup.Remove(path))
                {
                    ThrowHelper.ThrowAccessNotAllowed(path);
                }

                file = (InMemoryFileSystemFile)info;
            }
        }
        finally
        {
            if (file is not null)
            {
                // Dispatch event
                Dispatcher.RaiseEvent(new FileSystemEventArgs(
                    WatcherChangeTypes.Deleted,
                    file.Path,
                    name));
            }
            Unlock();
        }
    }

    public IFileSystemDirectory GetDirectory(DirectoryName name)
    {
        Lock(LockPolicy.Read);

        try
        {
            FileSystemPath path = FormatPath(name);

            if (!_lookup.TryGetValue(path, out InMemoryFileSystemInfo? info))
            {
                ThrowHelper.ThrowPathNotFound(path);
            }

            if (info is not InMemoryFileSystemDirectory)
            {
                ThrowHelper.ThrowPathNotFound(path);
            }

            return (InMemoryFileSystemDirectory)info;
        }
        finally
        {
            Unlock();
        }
    }

    public IFileSystemFile GetFile(FileName name)
    {
        Lock(LockPolicy.Read);

        try
        {
            FileSystemPath path = FormatPath(name);

            if (!_lookup.TryGetValue(path, out InMemoryFileSystemInfo? info))
            {
                ThrowHelper.ThrowPathNotFound(path);
            }

            if (info is not InMemoryFileSystemFile file)
            {
                ThrowHelper.ThrowPathNotFound(path);
            }

            return (InMemoryFileSystemFile)info;
        }
        finally
        {
            Unlock();
        }
    }

    public IEnumerable<IFileSystemDirectory> GetDirectories()
    {
        return _children.Values.OfType<InMemoryFileSystemDirectory>();
    }

    public IEnumerable<IFileSystemFile> GetFiles()
    {
        return _children.Values.OfType<InMemoryFileSystemFile>();
    }

    public IEnumerable<IFileSystemInfo> EnumerateFileSystem(FileSystemEnumerationOptions? options = null)
    {
        options ??= new FileSystemEnumerationOptions();
        if (options.Recurse)
        {
            return _children.Values
                .Where(item => !item.Attributes.HasFlag(options.AttributesToSkip))
                .SelectMany(child =>
                {
                    if (child is InMemoryFileSystemDirectory dir)
                    {
                        return dir.EnumerateFileSystem(options);
                    }
                    return new IFileSystemInfo[] { child };
                });
        }

        return _children.Values;
                //.Where(item => !item.Attributes.HasFlag(options.AttributesToSkip));
    }

    public IEnumerator<IFileSystemInfo> GetEnumerator()
    {
        return EnumerateFileSystem().GetEnumerator();
    }

    IEnumerator IEnumerable.GetEnumerator()
    {
        return GetEnumerator();
    }

    public override void Dispose()
    {
      
    }

    private FileSystemPath FormatPath(FileSystemPath path)
    {
        if (Parent is null)
        {
            return path;
        }

        return Parent.Path.Join(path);
    }
}

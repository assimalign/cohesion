using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Xml.Linq;

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

            // TODO: Raise Directory created
            // ... {code}

            return directory;
        }
        finally
        {
            Unlock();
        }
    }

    public IFileSystemFile CreateFile(FileName name)
    {
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
                    name));
            }

            Unlock();
        }
    }

    public void DeleteDirectory(DirectoryName name)
    {
        // Place a exclusive lock on the directory, and recurse into children
        // If another process has a lock on any child, this will wait until the lock is released or throw an unauthorized access exception
        Lock(LockPolicy.Exclusive, recurse: true);

        try
        {
            FileSystemPath path = FormatPath(name);

            if (!_lookup.TryGetValue(path, out InMemoryFileSystemInfo? info))
            {
                ThrowHelper.ThrowPathNotFound(path);
            }

            if (info is not InMemoryFileSystemDirectory directory)
            {
                ThrowHelper.ThrowPathNotFound(path);
            }

            else
            {
                if (!_lookup.Remove(path))
                {
                    ThrowHelper.ThrowAccessNotAllowed(path);
                }

                // Dispatch event
                Dispatcher.RaiseEvent(new FileSystemEventArgs(
                    WatcherChangeTypes.Deleted,
                    path,
                    name));
            }

        }
        finally
        {
            // TODO: Raise Directory created
            // ... {code}

            Unlock(recurse: true);
        }
    }

    public void DeleteFile(FileName name)
    {
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

            if (info is not InMemoryFileSystemFile f)
            {
                ThrowHelper.ThrowPathNotFound(path);
            }

            else
            {
                if (!_lookup.Remove(path))
                {
                    ThrowHelper.ThrowAccessNotAllowed(path);
                }

                file = f;
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

            if (info is not InMemoryFileSystemFile)
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

        return _children.Values
                .Where(item => !item.Attributes.HasFlag(options.AttributesToSkip));
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
        if (!IsLocked)
        {

        }
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

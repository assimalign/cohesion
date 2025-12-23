using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;

namespace Assimalign.Cohesion.FileSystem;

using Assimalign.Cohesion.FileSystem.Internal;
using Assimalign.Cohesion.Internal;

[DebuggerDisplay("{Name} - {Size}")]
[DebuggerTypeProxy(typeof(DebugView))]
public sealed partial class InMemoryFileSystem : InMemoryFileSystemLockHandle, IFileSystem
{
    // The locking strategy is based on https://www.kernel.org/doc/Documentation/filesystems/directory-locking

    private readonly Lock _lock = new Lock();
    private readonly InMemoryFileSystemDirectory _root;
    private readonly string _name;
    private readonly bool _isReadOnly;
    private readonly CultureInfo _cultureInfo;
    private readonly bool _ignoreCase;
    private Size _size;
    private Size _spaceUsed;
    private bool _isDisposed;

    /// <summary>
    /// 
    /// </summary>
    /// <param name="options"></param>
    public InMemoryFileSystem(InMemoryFileSystemOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);

        _name = options.Name ?? nameof(InMemoryFileSystem);
        _size = options.Size;
        _isReadOnly = options.IsReadOnly;
        _cultureInfo = options.CultureInfo ?? CultureInfo.InvariantCulture;
        _ignoreCase = options.IgnoreCase;
        _root = new InMemoryFileSystemDirectory(options.RootPath, this, _cultureInfo, _ignoreCase)
        {
            IgnoreAttributes = options.IgnoreAttributes,
        };
    }


    public string Name
    {
        get
        {
            CheckIfDisposed();
            return _name;
        }
    }

    public bool IsReadOnly
    {
        get
        {
            CheckIfDisposed();
            return _isReadOnly;
        }
    }

    public Size Size
    {
        get
        {
            CheckIfDisposed();
            return _size;
        }
    }

    public Size SpaceAvailable
    {
        get
        {
            CheckIfDisposed();
            return _size - _spaceUsed;
        }
    }

    public Size SpaceUsed
    {
        get
        {
            CheckIfDisposed();
            return _spaceUsed;
        }
    }

    public IFileSystemDirectory RootDirectory
    {
        get
        {
            CheckIfDisposed();
            return _root;
        }
    }

    public bool Exists(FileSystemPath path)
    {
        CheckIfDisposed();
        using var manager = new InMemoryFileSystemLockManager();

        // When check if an object exist in the file system we need to lock the entire path for delete operations
        // Whether the object is written to
        manager.Lock(this, LockPolicy.Delete);

        try
        {
            // Format the path
            FileSystemPath absolute = FormatPath(path);

            // Get Relative path
            FileSystemPath relative = absolute.Subpath(_root.Path.Length + 1);

            // Set the current path to begin from
            FileSystemPath current = _root.Path;

            // Get all directories in the path
            string[] names = relative.GetSegments();

            InMemoryFileSystemInfo state = _root;

            for (int i = 0; i < names.Length; i++)
            {
                current += names[i];

                bool isLast = current.Equals(absolute, _cultureInfo, _ignoreCase);

                if (state is not InMemoryFileSystemDirectory directory)
                {
                    return false;
                }
                if (!directory.Lookup.TryGetValue(current, out var info))
                {
                    return false;
                }
                else
                {
                    state = info;
                }

                if (!isLast)
                {
                    manager.Lock(state, LockPolicy.Delete);
                }
            }

            return true;
        }
        finally
        {
            manager.Dispose();
        }
    }

    public void CopyFile(FileSystemPath source, FileSystemPath destination)
    {
        CheckIfDisposed();
        CheckIfReadOnly(nameof(CreateDirectory));
    }

    public void Move(FileSystemPath source, FileSystemPath destination)
    {
        CheckIfDisposed();
        CheckIfReadOnly(nameof(CreateDirectory));
        // RootDirectory.Move(source, destination);
    }

    public IFileSystemEventToken Watch(Glob? pattern)
    {
        CheckIfDisposed();
        throw new NotImplementedException();
    }

    public IFileSystemDirectory CreateDirectory(FileSystemPath path)
    {
        CheckIfDisposed();
        CheckIfReadOnly(nameof(CreateDirectory));

        using var manager = new InMemoryFileSystemLockManager();

        // Lock operations that modify the directory structure, Reads are Allowed
        manager.Lock(this, LockPolicy.Write | LockPolicy.Delete);

        InMemoryFileSystemDirectory? result = default;

        try
        {
            // Get the absolute path
            FileSystemPath absolute = FormatPath(path);

            // Get Relative path
            FileSystemPath relative = absolute.Subpath(_root.Path.Length + 1);

            // Get all directories in the path
            DirectoryName[] directories = relative.GetDirectoryNames();

            // Set the current path to begin from
            FileSystemPath current = _root.Path;

            InMemoryFileSystemDirectory parent = _root;

            for (int i = 0; i < directories.Length; i++)
            {
                current += directories[i];

                InMemoryFileSystemDirectory? existingOrCreated = default!;

                // Set flags
                bool isLast = current.Equals(absolute, _cultureInfo, _ignoreCase);

                // Lock parent
                manager.Lock(parent, LockPolicy.Write | LockPolicy.Delete);

                // Get the current path
                DirectoryName name = directories[i];

                if (!parent.Lookup.TryGetValue(current, out InMemoryFileSystemInfo? info))
                {
                    parent.Entries[current] = existingOrCreated = new InMemoryFileSystemDirectory(name, parent, this);
                }
                else if (info is InMemoryFileSystemFile || isLast)
                {
                    // TODO: Maybe use a different exception to disallow creating a directory over a file.
                    ThrowHelper.ThrowFileOrDirectoryAlreadyExists(absolute);
                }
                else
                {
                    existingOrCreated = ((InMemoryFileSystemDirectory)info);
                }

                if (isLast)
                {
                    result = existingOrCreated;
                }
                else
                {
                    parent = existingOrCreated;
                }
            }

            return result!;
        }
        finally
        {
            if (result is not null)
            {
                //_root.Dispatcher.RaiseEvent(new FileSystemEventArgs(WatcherChangeTypes)
                //{

                //});
            }
            manager.Dispose();
        }
    }

    public IFileSystemFile CreateFile(FileSystemPath path)
    {
        CheckIfDisposed();
        CheckIfReadOnly(nameof(CreateFile));

        using var manager = new InMemoryFileSystemLockManager();

        // Lock operations that modify the directory structure, Reads are Allowed
        manager.Lock(this, LockPolicy.Write | LockPolicy.Delete);

        InMemoryFileSystemFile result = default!;

        try
        {
            // Get the absolute path
            FileSystemPath absolute = FormatPath(path);

            // Get Relative path
            FileSystemPath relative = absolute.Subpath(_root.Path.Length + 1);

            // Set the current path to begin from
            FileSystemPath current = _root.Path;

            // Get all directories in the path
            string[] segments = relative.GetSegments();

            InMemoryFileSystemDirectory parent = _root;

            for (int i = 0; i < segments.Length; i++)
            {
                current += segments[i];

                // Set flags
                bool isLast = current.Equals(absolute, _cultureInfo, _ignoreCase);

                manager.Lock(parent, LockPolicy.Write | LockPolicy.Delete);

                InMemoryFileSystemFile file = default!;
                InMemoryFileSystemDirectory existingOrCreated = parent;

                // If no entry exists, create one
                if (!parent.Lookup.TryGetValue(current, out InMemoryFileSystemInfo? info))
                {
                    if (isLast)
                    {
                        parent.Entries[current] = file = new InMemoryFileSystemFile(segments[i], parent, this);
                    }
                    else
                    {
                        parent.Entries[current] = existingOrCreated = new InMemoryFileSystemDirectory(segments[i], parent, this);
                    }
                    //isNew = true;
                }
                else if (info is InMemoryFileSystemFile && isLast)
                {
                    // TODO: Maybe use a different exception to disallow creating a directory over a file.
                    ThrowHelper.ThrowFileOrDirectoryAlreadyExists(absolute);
                }
                else
                {
                    existingOrCreated = (InMemoryFileSystemDirectory)info;
                }

                if (file is not null && isLast)
                {
                    result = file;
                }
                else
                {
                    parent = existingOrCreated;
                }
            }

            return result!;
        }
        finally
        {
            manager.Dispose();
        }
    }

    public void DeleteDirectory(FileSystemPath path)
    {
        CheckIfDisposed();
        CheckIfReadOnly(nameof(DeleteDirectory));

        // Place a exclusive lock on the directory, and recurse into children
        // If another process has a lock on any child, this will wait until the lock is released or throw an unauthorized access exception
        using var manager = new InMemoryFileSystemLockManager();

        // Lock operations that modify the directory structure, Reads are Allowed
        manager.Lock(this, LockPolicy.Write | LockPolicy.Delete);


        InMemoryFileSystemFile result = default!;

        try
        {
            // Get the absolute path
            FileSystemPath absolute = FormatPath(path);

            // Get Relative path
            FileSystemPath relative = absolute.Subpath(_root.Path.Length + 1);

            // Get all directories in the path
            DirectoryName[] directories = relative.GetDirectoryNames();

            // Set the current path to begin from
            FileSystemPath current = _root.Path;



            //directory = (InMemoryFileSystemDirectory)GetDirectory(path);

            //var transaction = new FileSystemTransaction<InMemoryFileSystemDirectory, InMemoryFileSystemInfo>((parent, context) =>
            //{
            //    if (!context.Lookup.Remove(parent.Path))
            //    {
            //        throw new Exception("Failed to remove directory from lookup.");
            //    }
            //    return parent;
            //});


            //foreach (var item in directory.EnumerateFileSystem())
            //{
            //    //transaction.Enqueue(0, (parent, context, next) =>
            //    //{
            //    //    // Begin Child Lock. Lock the directory for writing and deleting
            //    //    parent.Lock(LockPolicy.Write | LockPolicy.Delete, recurse: true);
            //    //    var child = next.Invoke(parent, context);
            //    //    parent.Unlock(recurse: true);
            //    //    return child;
            //    //});
            //}


            //transaction.Enqueue(0, (parent, context, next) =>
            //{
            //    // Begin Child Lock. Lock the directory for writing and deleting
            //    parent.Lock(LockPolicy.Write | LockPolicy.Delete, recurse: true);
            //    var child = next.Invoke(parent, context);
            //    parent.Unlock(recurse: true);
            //    return child;
            //});


            //transaction.Commit(directory, new InMemoryFileSystemTransactionContext<InMemoryFileSystemInfo>()
            //{
            //    Entries = _entries,
            //    Lookup = _lookup
            //});




            //foreach (var item in directory.EnumerateFileSystem(new FileSystemEnumerationOptions() {  Recurse = true }))
            //{
            //    if (!_lookup.Remove(item.Path))
            //    {
            //        ((InMemoryFileSystemInfo)item).Dispatcher.
            //    }
            //}

            //if (!_lookup.Remove(path))
            //{


        }
        finally
        {
            //if (directory is not null)
            //{
            //    // Dispatch event
            //    Dispatcher.RaiseEvent(new FileSystemEventArgs(
            //        WatcherChangeTypes.Deleted,
            //        directory.Path,
            //        directory.Name));
            //}

            Unlock();
        }
    }

    public void DeleteFile(FileSystemPath path)
    {
        CheckIfDisposed();
        CheckIfReadOnly(nameof(DeleteDirectory));

        // Place a exclusive lock on the directory, and recurse into children
        // If another process has a lock on any child, this will wait until the lock is released or throw an unauthorized access exception
        using var manager = new InMemoryFileSystemLockManager();

        // Lock operations that modify the directory structure, Reads are Allowed
        manager.Lock(this, LockPolicy.Exclusive);


        InMemoryFileSystemFile result = default!;

        try
        {
            // Get the absolute path
            FileSystemPath absolute = FormatPath(path);

            // Get Relative path
            FileSystemPath relative = absolute.Subpath(_root.Path.Length + 1);

            // Get all directories in the path
            DirectoryName[] directories = relative.GetDirectoryNames();

            // Set the current path to begin from
            FileSystemPath current = _root.Path;

            // Get all directories in the path
            string[] segments = relative.GetSegments();

            InMemoryFileSystemDirectory parent = _root;

            for (int i = 0; i < segments.Length; i++)
            {
                current += segments[i];

                // Set flags
                bool isLast = current.Equals(absolute, _cultureInfo, _ignoreCase);

                manager.Lock(parent, LockPolicy.Write | LockPolicy.Delete);

                InMemoryFileSystemFile file = default!;
                InMemoryFileSystemDirectory existingOrCreated = parent;

                // If no entry exists, Throw exception
                if (!parent.Lookup.TryGetValue(current, out InMemoryFileSystemInfo? info))
                {
                    FileSystemException.ThrowFileNotFound(absolute);
                }
                else if (info is not InMemoryFileSystemFile && isLast)
                {
                    FileSystemException.ThrowFileNotFound(absolute);
                }
                else if (!isLast)
                {
                    existingOrCreated = (InMemoryFileSystemDirectory)info;
                }

                if (file is not null && isLast)
                {
                    manager.Lock(file, LockPolicy.Exclusive);

                    if (!parent.Entries.Remove(current))
                    {
                        // ThrowHelper.ThrowIOException(new IOException("Failed to remove file from entries."));
                    }

                    result = file;
                }
                else
                {
                    parent = existingOrCreated;
                }
            }
        }
        finally
        {
            manager.Dispose();
        }
    }

    public IFileSystemInfo GetInfo(FileSystemPath path)
    {
        CheckIfDisposed();
        CheckIfReadOnly(nameof(DeleteDirectory));

        // Place a exclusive lock on the directory, and recurse into children
        // If another process has a lock on any child, this will wait until the lock is released or throw an unauthorized access exception
        using var manager = new InMemoryFileSystemLockManager();

        // Lock operations that modify the directory structure, Reads are Allowed
        manager.Lock(this, LockPolicy.Write | LockPolicy.Delete);


        InMemoryFileSystemInfo result = _root!;

        try
        {
            // Format the path
            FileSystemPath absolute = FormatPath(path);

            // Get Relative path
            FileSystemPath relative = absolute.Subpath(_root.Path.Length + 1);

            // Set the current path to begin from
            FileSystemPath current = _root.Path;

            // Get all directories in the path
            string[] names = relative.GetSegments();

            for (int i = 0; i < names.Length; i++)
            {
                current += names[i];

                bool isLast = current.Equals(absolute, _cultureInfo, _ignoreCase);

                if (result is not InMemoryFileSystemDirectory directory)
                {
                    ThrowHelper.ThrowPathNotFound(absolute);
                }
                else if (!directory.Lookup.TryGetValue(current, out var info))
                {
                    ThrowHelper.ThrowPathNotFound(absolute);
                }
                else
                {
                    result = info;
                }

                if (!isLast)
                {
                    manager.Lock(result, LockPolicy.Write | LockPolicy.Delete);
                }
            }

            return result;
        }
        finally
        {
            manager.Dispose();
        }
    }

    public IFileSystemFile GetFile(FileSystemPath path)
    {
        CheckIfDisposed();
        InMemoryFileSystemFile? file = default!;

        if (GetInfo(path) is not InMemoryFileSystemFile info)
        {
            FileSystemException.ThrowFileNotFound(path);
        }
        else if (info is not null)
        {
            file = info;
        }
        return file;
    }

    public IFileSystemDirectory GetDirectory(FileSystemPath path)
    {
        CheckIfDisposed();
        InMemoryFileSystemDirectory? directory = default!;

        if (GetInfo(path) is not InMemoryFileSystemDirectory info)
        {
            FileSystemException.ThrowDirectoryNotFound(path);
        }
        else if (info is not null)
        {
            directory = info;
        }

        return directory;
    }

    IEnumerator IEnumerable.GetEnumerator()
    {
        return GetEnumerator();
    }
    public void Dispose()
    {
        CheckIfDisposed();
        Lock(LockPolicy.Exclusive);

        try
        {
            _root.Dispose();

        }
        finally
        {
            _isDisposed = true;
            Unlock();
        }
    }
    public ValueTask DisposeAsync()
    {
        Dispose();
        return ValueTask.CompletedTask;
    }

    public IEnumerable<IFileSystemInfo> EnumerateFileSystem(FileSystemEnumerationOptions? options = null)
    {
        CheckIfDisposed();
        Lock(LockPolicy.Delete);

        try
        {
            return _root.EnumerateFileSystem(options);
        }
        finally
        {
            Unlock();
        }
    }

    public IEnumerator<IFileSystemInfo> GetEnumerator()
    {
        return EnumerateFileSystem().GetEnumerator();
    }

    internal void IncrementSpaceUsed(Size value)
    {
        lock (_lock)
        {
            if ((_spaceUsed + value) > SpaceAvailable)
            {
                ThrowHelper.ThrowNotEnoughSpace(new IOException("There is not enough space in the file system to complete this operation."));
            }

            _spaceUsed += value;
        }
    }

    private FileSystemPath FormatPath(FileSystemPath path)
    {
        FileSystemPath parentPath = _root.Path;

        return parentPath.Merge(path, _cultureInfo, _ignoreCase);
    }
    private void CheckIfReadOnly(string? operation = null)
    {
        lock (_lock)
        {   
            if (_isReadOnly)
            {
                ThrowHelper.ThrowInvalidOperationException($"The operation {operation} is not allowed. FileSystem is read-only.");
            }
        }
    }

    private void CheckIfDisposed()
    {
        lock (_lock)
        {
            ObjectDisposedException.ThrowIf(_isDisposed, this);
        }
    }

    private sealed class DebugView
    {
        private readonly InMemoryFileSystem _fileSystem;

        public DebugView(InMemoryFileSystem fileSystem)
        {
            _fileSystem = fileSystem;
        }
        public Size Size => _fileSystem.Size;
        public Size SpaceAvailable => _fileSystem.SpaceAvailable;
        public Size SpaceUsed => _fileSystem.SpaceUsed;
        public string Name => _fileSystem.Name;
        public bool IsReadOnly => _fileSystem.IsReadOnly;
        public InMemoryFileSystemDirectory RootDirectory => (InMemoryFileSystemDirectory)_fileSystem.RootDirectory;

        [DebuggerBrowsable(DebuggerBrowsableState.Collapsed)]
        public InMemoryFileSystemInfo[] Entries => _fileSystem.Cast<InMemoryFileSystemInfo>().ToArray();
    }
}

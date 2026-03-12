using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace Assimalign.Cohesion.FileSystem;

using Assimalign.Cohesion.FileSystem.Internal;
using Assimalign.Cohesion.Internal;

/// <summary>
/// An in-memory implementation of <see cref="IFileSystem"/>.
/// </summary>
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
    /// Creates a new in-memory file system with the specified options.
    /// </summary>
    /// <param name="options">The configuration options for the in-memory file system.</param>
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

    /// <inheritdoc />
    public string Name
    {
        get
        {
            CheckIfDisposed();
            return _name;
        }
    }

    /// <inheritdoc />
    public bool IsReadOnly
    {
        get
        {
            CheckIfDisposed();
            return _isReadOnly;
        }
    }

    /// <inheritdoc />
    public Size Size
    {
        get
        {
            CheckIfDisposed();
            return _size;
        }
    }

    /// <inheritdoc />
    public Size SpaceAvailable
    {
        get
        {
            CheckIfDisposed();
            return _size - _spaceUsed;
        }
    }

    /// <inheritdoc />
    public Size SpaceUsed
    {
        get
        {
            CheckIfDisposed();
            return _spaceUsed;
        }
    }

    /// <inheritdoc />
    public IFileSystemDirectory RootDirectory
    {
        get
        {
            CheckIfDisposed();
            return _root;
        }
    }

    /// <inheritdoc />
    public bool Exists(FileSystemPath path)
    {
        CheckIfDisposed();
        using var manager = new InMemoryFileSystemLockManager();

        manager.Lock(this, LockPolicy.Delete);

        try
        {
            FileSystemPath absolute = FormatPath(path);
            FileSystemPath relative = GetRelativePath(absolute);
            FileSystemPath current = _root.Path;
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

    /// <inheritdoc />
    public void CopyFile(FileSystemPath source, FileSystemPath destination)
    {
        CheckIfDisposed();
        CheckIfReadOnly(nameof(CopyFile));

        // Get the source file first
        var sourceInfo = GetInfo(source);
        if (sourceInfo is not InMemoryFileSystemFile sourceFile)
        {
            FileSystemException.ThrowFileNotFound(source);
            return;
        }

        // Create the destination file
        var destFile = (InMemoryFileSystemFile)CreateFile(destination);

        // Copy the content
        destFile.Content.CopyFrom(sourceFile.Content);
    }

    /// <inheritdoc />
    public void Move(FileSystemPath source, FileSystemPath destination)
    {
        CheckIfDisposed();
        CheckIfReadOnly(nameof(Move));

        using var manager = new InMemoryFileSystemLockManager();

        manager.Lock(this, LockPolicy.Write | LockPolicy.Delete);

        try
        {
            FileSystemPath absoluteSource = FormatPath(source);
            FileSystemPath absoluteDest = FormatPath(destination);

            // Navigate to source entry
            var sourceEntry = NavigateToEntry(absoluteSource, manager);
            if (sourceEntry is null)
            {
                ThrowHelper.ThrowPathNotFound(source);
            }

            // Find the parent directory of the source
            var sourceParent = FindParentDirectory(absoluteSource, manager);
            if (sourceParent is null)
            {
                ThrowHelper.ThrowPathNotFound(source);
            }

            // Check destination doesn't already exist
            if (Exists(destination))
            {
                ThrowHelper.ThrowFileOrDirectoryAlreadyExists(destination);
            }

            // Remove from source parent
            sourceParent.Entries.Remove(absoluteSource);

            // Recreate at destination
            if (sourceEntry is InMemoryFileSystemFile sourceFile)
            {
                var destFile = (InMemoryFileSystemFile)CreateFile(destination);
                destFile.Content.CopyFrom(sourceFile.Content);
            }
            else if (sourceEntry is InMemoryFileSystemDirectory sourceDir)
            {
                CreateDirectory(destination);
                CopyDirectoryContents(sourceDir, absoluteDest);
            }
        }
        finally
        {
            manager.Dispose();
        }
    }

    /// <inheritdoc />
    public IFileSystemEventToken Watch(Glob? pattern)
    {
        CheckIfDisposed();
        return _root.Watch(pattern);
    }

    /// <inheritdoc />
    public IFileSystemDirectory CreateDirectory(FileSystemPath path)
    {
        CheckIfDisposed();
        CheckIfReadOnly(nameof(CreateDirectory));

        using var manager = new InMemoryFileSystemLockManager();

        manager.Lock(this, LockPolicy.Write | LockPolicy.Delete);

        InMemoryFileSystemDirectory? result = default;

        try
        {
            FileSystemPath absolute = FormatPath(path);
            FileSystemPath relative = GetRelativePath(absolute);
            DirectoryName[] directories = relative.GetDirectoryNames();
            FileSystemPath current = _root.Path;

            InMemoryFileSystemDirectory parent = _root;

            for (int i = 0; i < directories.Length; i++)
            {
                current += directories[i];

                InMemoryFileSystemDirectory? existingOrCreated = default!;

                bool isLast = current.Equals(absolute, _cultureInfo, _ignoreCase);

                manager.Lock(parent, LockPolicy.Write | LockPolicy.Delete);

                DirectoryName name = directories[i];

                if (!parent.Lookup.TryGetValue(current, out InMemoryFileSystemInfo? info))
                {
                    parent.Entries[current] = existingOrCreated = new InMemoryFileSystemDirectory(name, parent, this);
                }
                else if (info is InMemoryFileSystemFile || isLast)
                {
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
                _root.Dispatcher.RaiseEvent(new FileSystemEventArgs(
                    WatcherChangeTypes.Created,
                    result.Path,
                    result.Name));
            }
            manager.Dispose();
        }
    }

    /// <inheritdoc />
    public IFileSystemFile CreateFile(FileSystemPath path)
    {
        CheckIfDisposed();
        CheckIfReadOnly(nameof(CreateFile));

        using var manager = new InMemoryFileSystemLockManager();

        manager.Lock(this, LockPolicy.Write | LockPolicy.Delete);

        InMemoryFileSystemFile result = default!;

        try
        {
            FileSystemPath absolute = FormatPath(path);
            FileSystemPath relative = GetRelativePath(absolute);
            FileSystemPath current = _root.Path;
            string[] segments = relative.GetSegments();

            InMemoryFileSystemDirectory parent = _root;

            for (int i = 0; i < segments.Length; i++)
            {
                current += segments[i];

                bool isLast = current.Equals(absolute, _cultureInfo, _ignoreCase);

                manager.Lock(parent, LockPolicy.Write | LockPolicy.Delete);

                InMemoryFileSystemFile file = default!;
                InMemoryFileSystemDirectory existingOrCreated = parent;

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
                }
                else if (info is InMemoryFileSystemFile && isLast)
                {
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
            if (result is not null)
            {
                _root.Dispatcher.RaiseEvent(new FileSystemEventArgs(
                    WatcherChangeTypes.Created,
                    result.Path,
                    result.Name));
            }
            manager.Dispose();
        }
    }

    /// <inheritdoc />
    public void DeleteDirectory(FileSystemPath path)
    {
        CheckIfDisposed();
        CheckIfReadOnly(nameof(DeleteDirectory));

        using var manager = new InMemoryFileSystemLockManager();

        manager.Lock(this, LockPolicy.Write | LockPolicy.Delete);

        InMemoryFileSystemDirectory? directory = default;

        try
        {
            FileSystemPath absolute = FormatPath(path);

            var entry = NavigateToEntry(absolute, manager);

            if (entry is not InMemoryFileSystemDirectory dir)
            {
                FileSystemException.ThrowDirectoryNotFound(path);
                return;
            }

            directory = dir;

            var parentDir = FindParentDirectory(absolute, manager);

            if (parentDir is null)
            {
                throw new InvalidOperationException("Cannot delete the root directory.");
            }

            // Lock the directory exclusively for deletion
            manager.Lock(directory, LockPolicy.Exclusive);

            // Recursively dispose all children
            foreach (var (key, child) in directory.Entries.ToArray())
            {
                child.Dispose();
            }

            directory.Entries.Clear();

            // Remove from parent
            parentDir.Entries.Remove(absolute);
        }
        finally
        {
            if (directory is not null)
            {
                _root.Dispatcher.RaiseEvent(new FileSystemEventArgs(
                    WatcherChangeTypes.Deleted,
                    directory.Path,
                    directory.Name));
            }
            manager.Dispose();
        }
    }

    /// <inheritdoc />
    public void DeleteFile(FileSystemPath path)
    {
        CheckIfDisposed();
        CheckIfReadOnly(nameof(DeleteFile));

        using var manager = new InMemoryFileSystemLockManager();

        manager.Lock(this, LockPolicy.Write | LockPolicy.Delete);

        InMemoryFileSystemFile? file = default;

        try
        {
            FileSystemPath absolute = FormatPath(path);

            var entry = NavigateToEntry(absolute, manager);

            if (entry is not InMemoryFileSystemFile foundFile)
            {
                FileSystemException.ThrowFileNotFound(path);
                return;
            }

            file = foundFile;

            var parentDir = FindParentDirectory(absolute, manager);

            if (parentDir is null)
            {
                FileSystemException.ThrowFileNotFound(path);
                return;
            }

            // Lock the file exclusively for deletion
            manager.Lock(file, LockPolicy.Exclusive);

            // Remove from parent
            parentDir.Entries.Remove(absolute);
        }
        finally
        {
            if (file is not null)
            {
                _root.Dispatcher.RaiseEvent(new FileSystemEventArgs(
                    WatcherChangeTypes.Deleted,
                    file.Path,
                    file.Name));
            }
            manager.Dispose();
        }
    }

    /// <inheritdoc />
    public IFileSystemInfo GetInfo(FileSystemPath path)
    {
        CheckIfDisposed();

        using var manager = new InMemoryFileSystemLockManager();

        manager.Lock(this, LockPolicy.Delete);

        InMemoryFileSystemInfo result = _root!;

        try
        {
            FileSystemPath absolute = FormatPath(path);
            FileSystemPath relative = GetRelativePath(absolute);
            FileSystemPath current = _root.Path;
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
                    manager.Lock(result, LockPolicy.Delete);
                }
            }

            return result;
        }
        finally
        {
            manager.Dispose();
        }
    }

    /// <inheritdoc />
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

    /// <inheritdoc />
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

    /// <inheritdoc />
    public void Dispose()
    {
        if (_isDisposed)
        {
            return;
        }

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

    /// <inheritdoc />
    public ValueTask DisposeAsync()
    {
        Dispose();
        return ValueTask.CompletedTask;
    }

    /// <inheritdoc />
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

    /// <inheritdoc />
    public IEnumerator<IFileSystemInfo> GetEnumerator()
    {
        return EnumerateFileSystem().GetEnumerator();
    }

    internal void IncrementSpaceUsed(long value)
    {
        lock (_lock)
        {
            long newUsed = _spaceUsed.Length + value;

            if (newUsed < 0)
            {
                newUsed = 0;
            }

            if (newUsed > _size.Length)
            {
                ThrowHelper.ThrowNotEnoughSpace(new IOException("There is not enough space in the file system to complete this operation."));
            }

            _spaceUsed = newUsed;
        }
    }

    private InMemoryFileSystemInfo? NavigateToEntry(FileSystemPath absolute, InMemoryFileSystemLockManager manager)
    {
        FileSystemPath relative = GetRelativePath(absolute);
        FileSystemPath current = _root.Path;
        string[] names = relative.GetSegments();

        InMemoryFileSystemInfo state = _root;

        for (int i = 0; i < names.Length; i++)
        {
            current += names[i];

            bool isLast = current.Equals(absolute, _cultureInfo, _ignoreCase);

            if (state is not InMemoryFileSystemDirectory directory)
            {
                return null;
            }
            if (!directory.Lookup.TryGetValue(current, out var info))
            {
                return null;
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

        return state;
    }

    private InMemoryFileSystemDirectory? FindParentDirectory(FileSystemPath absolute, InMemoryFileSystemLockManager manager)
    {
        string pathStr = absolute.ToString();
        int lastSep = pathStr.LastIndexOf('/');

        if (lastSep <= 0)
        {
            return _root;
        }

        // Check if the absolute path ends with '/' (directory), if so find second-to-last separator
        if (lastSep == pathStr.Length - 1)
        {
            lastSep = pathStr.LastIndexOf('/', lastSep - 1);
            if (lastSep <= 0)
            {
                return _root;
            }
        }

        FileSystemPath parentPath = pathStr[..lastSep] + "/";

        var entry = NavigateToEntry(parentPath, manager);
        return entry as InMemoryFileSystemDirectory;
    }

    private void CopyDirectoryContents(InMemoryFileSystemDirectory source, FileSystemPath destBase)
    {
        foreach (var (key, entry) in source.Entries)
        {
            string entryName = entry switch
            {
                InMemoryFileSystemFile f => f.Name.ToString(),
                InMemoryFileSystemDirectory d => d.Name.ToString(),
                _ => throw new InvalidOperationException()
            };

            FileSystemPath newPath = destBase.Join(entryName);

            if (entry is InMemoryFileSystemFile file)
            {
                var newFile = (InMemoryFileSystemFile)CreateFile(newPath);
                newFile.Content.CopyFrom(file.Content);
            }
            else if (entry is InMemoryFileSystemDirectory dir)
            {
                CreateDirectory(newPath);
                CopyDirectoryContents(dir, newPath);
            }
        }
    }

    private FileSystemPath FormatPath(FileSystemPath path)
    {
        FileSystemPath parentPath = _root.Path;

        return parentPath.Merge(path, _cultureInfo, _ignoreCase);
    }

    private FileSystemPath GetRelativePath(FileSystemPath absolute)
    {
        FileSystemPath rootPath = _root.Path;
        // When root is "/" the separator is already included, so don't add +1
        int offset = rootPath.Equals("/") ? rootPath.Length : rootPath.Length + 1;
        return absolute.Subpath(offset);
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

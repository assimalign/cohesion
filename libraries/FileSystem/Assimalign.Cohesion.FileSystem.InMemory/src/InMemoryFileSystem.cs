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
    private readonly object _watchGate = new();
    private readonly HashSet<InMemoryFileSystemEventToken> _watchTokens = new();
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

        // Resolve both ends before either is touched, so a refused path is refused whatever else
        // is wrong with the other one.
        FormatPath(source);
        FormatPath(destination);

        using var manager = new InMemoryFileSystemLockManager();

        manager.Lock(this, LockPolicy.Exclusive);

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

        manager.Lock(this, LockPolicy.Exclusive);

        try
        {
            FileSystemPath absoluteSource = FormatPath(source);
            FileSystemPath absoluteDest = FormatPath(destination);

            // Navigate to source entry
            var sourceEntry = NavigateToEntry(absoluteSource, manager);
            if (sourceEntry is null)
            {
                FileSystemException.ThrowPathNotFound(source);
            }

            // Find the parent directory of the source
            var sourceParent = FindParentDirectory(absoluteSource, manager);
            if (sourceParent is null)
            {
                FileSystemException.ThrowPathNotFound(source);
            }

            // Check destination doesn't already exist
            if (Exists(destination))
            {
                FileSystemException.ThrowPathAlreadyExist(destination);
            }

            if (sourceEntry is InMemoryFileSystemFile sourceFile)
            {
                var destinationParent = EnsureParentDirectory(absoluteDest);

                manager.Lock(sourceParent, LockPolicy.Write | LockPolicy.Delete);
                manager.Lock(destinationParent, LockPolicy.Write | LockPolicy.Delete);
                manager.Lock(sourceFile, LockPolicy.Exclusive);

                sourceFile.EnsureDeleteAllowed(absoluteSource);

                sourceParent.Entries.Remove(absoluteSource);
                sourceFile.MoveTo(absoluteDest.GetFileName()!.Value, destinationParent);
                destinationParent.Entries[absoluteDest] = sourceFile;
            }
            else if (sourceEntry is InMemoryFileSystemDirectory sourceDir)
            {
                CreateDirectory(destination);
                CopyDirectoryContents(sourceDir, absoluteDest);
                sourceParent.Entries.Remove(absoluteSource);
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

    internal IFileSystemEventToken CreateWatchToken(InMemoryFileSystemInfo info, Glob pattern)
    {
        lock (_watchGate)
        {
            CheckIfDisposed();
            var token = new InMemoryFileSystemEventToken(info, pattern, RemoveWatchToken);
            _watchTokens.Add(token);
            return token;
        }
    }

    private void RemoveWatchToken(InMemoryFileSystemEventToken token)
    {
        lock (_watchGate)
        {
            _watchTokens.Remove(token);
        }
    }

    /// <inheritdoc />
    public IFileSystemDirectory CreateDirectory(FileSystemPath path)
    {
        CheckIfDisposed();
        CheckIfReadOnly(nameof(CreateDirectory));

        using var manager = new InMemoryFileSystemLockManager();

        manager.Lock(this, LockPolicy.Exclusive);

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
                    FileSystemException.ThrowPathAlreadyExist(absolute);
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
                    result.Parent?.Path ?? _root.Path,
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

        manager.Lock(this, LockPolicy.Exclusive);

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
                    FileSystemException.ThrowPathAlreadyExist(absolute);
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
                    result.Directory.Path,
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

        manager.Lock(this, LockPolicy.Exclusive);

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
                    directory.Parent?.Path ?? _root.Path,
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

        manager.Lock(this, LockPolicy.Exclusive);

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
            file.BeginDelete(absolute);

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
            file.MarkDeleted();
        }
        finally
        {
            if (file is not null)
            {
                _root.Dispatcher.RaiseEvent(new FileSystemEventArgs(
                    WatcherChangeTypes.Deleted,
                    file.Directory.Path,
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
                    FileSystemException.ThrowPathNotFound(absolute);
                }
                else if (!directory.Lookup.TryGetValue(current, out var info))
                {
                    FileSystemException.ThrowPathNotFound(absolute);
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
        InMemoryFileSystemEventToken[] tokens;
        lock (_watchGate)
        {
            if (_isDisposed)
            {
                return;
            }
            _isDisposed = true;
            tokens = _watchTokens.ToArray();
            _watchTokens.Clear();
        }

        foreach (var token in tokens)
        {
            token.Dispose();
        }

        Lock(LockPolicy.Exclusive);

        try
        {
            _root.Dispose();
        }
        finally
        {
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
                FileSystemException.ThrowNotEnoughSpace(new IOException("There is not enough space in the file system to complete this operation."));
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

    private InMemoryFileSystemDirectory EnsureParentDirectory(FileSystemPath absolute)
    {
        string path = absolute.ToString();
        int lastSep = path.LastIndexOf('/');

        if (lastSep <= 0)
        {
            return _root;
        }

        FileSystemPath parentPath = path[..lastSep];

        if (!Exists(parentPath))
        {
            return (InMemoryFileSystemDirectory)CreateDirectory(parentPath);
        }

        return (InMemoryFileSystemDirectory)GetDirectory(parentPath);
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

    /// <summary>
    /// Resolves <paramref name="path"/> to an absolute path in this file system's namespace and
    /// throws <see cref="FileSystemErrorCode.PathOutsideRoot"/> unless it is the root or lies under
    /// it on a segment boundary. A relative path is taken from the root; <c>.</c> and <c>..</c>
    /// segments are resolved before the check, and <c>..</c> at the namespace root stays there, as
    /// <c>/..</c> is <c>/</c> on every host. The result is rebuilt from the root's own text, so tree
    /// lookups always start from the stored root.
    /// </summary>
    private FileSystemPath FormatPath(FileSystemPath path)
    {
        string root = _root.Path.ToString();
        string value = path.ToString();

        if (string.IsNullOrEmpty(value))
        {
            return _root.Path;
        }

        string namespaceRoot;
        string segments;

        if (path.HasRoot(out string pathRoot))
        {
            namespaceRoot = pathRoot;
            segments = value[pathRoot.Length..];
        }
        else
        {
            namespaceRoot = _root.Path.HasRoot(out string rootRoot) ? rootRoot : string.Empty;
            segments = string.Concat(root.AsSpan(namespaceRoot.Length), "/", value);
        }

        string normalized = Normalize(namespaceRoot, segments);

        if (!IsUnderRoot(normalized, root))
        {
            FileSystemException.ThrowPathOutsideRoot(path);
        }

        return string.Concat(root, normalized.AsSpan(root.Length));
    }

    // Resolves "." and ".." segments lexically beneath namespaceRoot.
    private static string Normalize(string namespaceRoot, string path)
    {
        var resolved = new List<string>();
        ReadOnlySpan<char> span = path;

        foreach (Range range in span.SplitAny('/', '\\'))
        {
            ReadOnlySpan<char> segment = span[range];

            if (segment.IsEmpty || segment is ".")
            {
                continue;
            }

            if (segment is "..")
            {
                if (resolved.Count > 0)
                {
                    resolved.RemoveAt(resolved.Count - 1);
                }

                continue;
            }

            resolved.Add(segment.ToString());
        }

        string joined = string.Join('/', resolved);

        if (namespaceRoot.Length == 0 || joined.Length == 0)
        {
            return namespaceRoot.Length == 0 ? joined : namespaceRoot;
        }

        return namespaceRoot.EndsWith('/') ? namespaceRoot + joined : namespaceRoot + "/" + joined;
    }

    // True when path is root or lies under it on a segment boundary. The match is ordinal (ignoring
    // case when the file system does): a culture-aware match can succeed across ignorable characters
    // with a length that does not line up with a separator.
    private bool IsUnderRoot(string path, string root)
    {
        if (!path.StartsWith(root, _ignoreCase ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal))
        {
            return false;
        }

        return path.Length == root.Length
            || root.EndsWith('/')
            || path[root.Length] == '/';
    }

    private FileSystemPath GetRelativePath(FileSystemPath absolute)
    {
        string rootPath = _root.Path.ToString();
        string text = absolute.ToString();

        // FormatPath only produces the root or a path under it, so a path no longer than the root
        // is the root itself.
        if (text.Length <= rootPath.Length)
        {
            return FileSystemPath.Empty;
        }

        // A root that ends in a separator ("/") is followed directly by the first segment.
        int offset = rootPath.EndsWith('/') ? rootPath.Length : rootPath.Length + 1;
        return absolute.Subpath(offset);
    }

    private void CheckIfReadOnly(string? operation = null)
    {
        lock (_lock)
        {
            if (_isReadOnly)
            {
                FileSystemException.ThrowReadOnly(operation ?? string.Empty);
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

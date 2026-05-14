using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.IO.IsolatedStorage;
using System.Threading.Tasks;

namespace Assimalign.Cohesion.FileSystem;

using Assimalign.Cohesion.FileSystem.Internal;

/// <summary>
/// <see cref="IFileSystem"/> implementation backed by an
/// <see cref="System.IO.IsolatedStorage.IsolatedStorageFile"/> store. The store is acquired
/// according to <see cref="IsolatedFileSystemOptions.Scope"/> and the supplied evidence types.
/// The provider exposes a Cohesion-style <see cref="FileSystemPath"/> surface ('/' separated,
/// rooted at "/") while delegating actual storage to the underlying isolated store.
/// </summary>
/// <remarks>
/// <para>
/// Several capabilities are not surfaced by <see cref="IsolatedStorageFile"/> and are reported
/// deterministically:
/// </para>
/// <list type="bullet">
/// <item><description><see cref="IFileSystemInfo.Attributes"/> /
/// <see cref="IFileSystemInfo.SetAttributes"/> throw <see cref="NotSupportedException"/>.</description></item>
/// <item><description><see cref="Watch"/> returns a noop token that never fires.</description></item>
/// </list>
/// </remarks>
[DebuggerDisplay("{Name} - Size: {Size}, Used: {SpaceUsed}")]
public sealed class IsolatedFileSystem : IFileSystem
{
    private readonly IsolatedStorageFile _storage;
    private readonly IsolatedFileSystemDirectory _root;
    private readonly string _name;
    private readonly bool _isReadOnly;
    private readonly bool _removeStoreOnDispose;
    private bool _isDisposed;

    /// <summary>
    /// Creates a new <see cref="IsolatedFileSystem"/> using the default user+assembly scope.
    /// </summary>
    public IsolatedFileSystem()
        : this(new IsolatedFileSystemOptions())
    {
    }

    /// <summary>
    /// Creates a new <see cref="IsolatedFileSystem"/> using the supplied <paramref name="options"/>.
    /// </summary>
    /// <param name="options">Configuration for the store and its read-only behavior.</param>
    /// <exception cref="ArgumentNullException">Thrown when <paramref name="options"/> is null.</exception>
    public IsolatedFileSystem(IsolatedFileSystemOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);

        _name = options.Name ?? nameof(IsolatedFileSystem);
        _isReadOnly = options.IsReadOnly;
        _removeStoreOnDispose = options.RemoveStoreOnDispose;
        _storage = OpenStore(options);
        _root = new IsolatedFileSystemDirectory(this, _storage, IsolatedPathHelper.Root);
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
            return _storage.Quota;
        }
    }

    /// <inheritdoc />
    public Size SpaceAvailable
    {
        get
        {
            CheckIfDisposed();
            return _storage.AvailableFreeSpace;
        }
    }

    /// <inheritdoc />
    public Size SpaceUsed
    {
        get
        {
            CheckIfDisposed();
            return _storage.UsedSize;
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
        string store = IsolatedPathHelper.ToStorePath(path);

        // The root always exists; the store-relative form is the empty string.
        if (string.IsNullOrEmpty(store))
        {
            return true;
        }

        return _storage.FileExists(store) || _storage.DirectoryExists(store);
    }

    /// <inheritdoc />
    public IFileSystemDirectory CreateDirectory(FileSystemPath path)
    {
        CheckIfDisposed();
        CheckIfReadOnly(nameof(CreateDirectory));

        FileSystemPath absolute = IsolatedPathHelper.ToAbsolute(path);
        string store = IsolatedPathHelper.ToStorePath(absolute);

        if (string.IsNullOrEmpty(store))
        {
            // Caller asked for the root — it already exists; mirror the InMemory contract that
            // duplicates surface as Conflict.
            FileSystemException.ThrowPathAlreadyExist(absolute);
        }

        if (_storage.DirectoryExists(store) || _storage.FileExists(store))
        {
            FileSystemException.ThrowPathAlreadyExist(absolute);
        }

        try
        {
            // IsolatedStorageFile.CreateDirectory auto-creates intermediate directories which
            // matches the shared FileSystemStandardTests contract.
            _storage.CreateDirectory(store);
        }
        catch (IsolatedStorageException ex)
        {
            FileSystemException.ThrowAccessDenied(absolute, ex);
        }
        catch (UnauthorizedAccessException ex)
        {
            FileSystemException.ThrowAccessDenied(absolute, ex);
        }
        catch (DirectoryNotFoundException ex)
        {
            FileSystemException.ThrowDirectoryNotFound(absolute, ex);
        }

        return new IsolatedFileSystemDirectory(this, _storage, absolute);
    }

    /// <inheritdoc />
    public IFileSystemFile CreateFile(FileSystemPath path)
    {
        CheckIfDisposed();
        CheckIfReadOnly(nameof(CreateFile));

        FileSystemPath absolute = IsolatedPathHelper.ToAbsolute(path);
        string store = IsolatedPathHelper.ToStorePath(absolute);

        if (string.IsNullOrEmpty(store))
        {
            FileSystemException.ThrowPathAlreadyExist(absolute);
        }

        if (_storage.FileExists(store) || _storage.DirectoryExists(store))
        {
            FileSystemException.ThrowPathAlreadyExist(absolute);
        }

        // IsolatedStorageFile.CreateFile does not auto-create the parent chain; mirror the
        // InMemory + Physical providers and ensure intermediate directories exist first.
        EnsureParentDirectory(store);

        try
        {
            using var stream = _storage.CreateFile(store);
        }
        catch (IsolatedStorageException ex)
        {
            FileSystemException.ThrowAccessDenied(absolute, ex);
        }
        catch (UnauthorizedAccessException ex)
        {
            FileSystemException.ThrowAccessDenied(absolute, ex);
        }
        catch (DirectoryNotFoundException ex)
        {
            FileSystemException.ThrowDirectoryNotFound(absolute, ex);
        }

        return new IsolatedFileSystemFile(this, _storage, absolute);
    }

    /// <inheritdoc />
    public void DeleteDirectory(FileSystemPath path)
    {
        CheckIfDisposed();
        CheckIfReadOnly(nameof(DeleteDirectory));

        FileSystemPath absolute = IsolatedPathHelper.ToAbsolute(path);
        string store = IsolatedPathHelper.ToStorePath(absolute);

        if (string.IsNullOrEmpty(store) || !_storage.DirectoryExists(store))
        {
            FileSystemException.ThrowDirectoryNotFound(absolute);
        }

        try
        {
            DeleteDirectoryRecursive(store);
        }
        catch (DirectoryNotFoundException ex)
        {
            FileSystemException.ThrowDirectoryNotFound(absolute, ex);
        }
        catch (UnauthorizedAccessException ex)
        {
            FileSystemException.ThrowAccessDenied(absolute, ex);
        }
        catch (IsolatedStorageException ex)
        {
            FileSystemException.ThrowAccessDenied(absolute, ex);
        }
    }

    /// <inheritdoc />
    public void DeleteFile(FileSystemPath path)
    {
        CheckIfDisposed();
        CheckIfReadOnly(nameof(DeleteFile));

        FileSystemPath absolute = IsolatedPathHelper.ToAbsolute(path);
        string store = IsolatedPathHelper.ToStorePath(absolute);

        if (string.IsNullOrEmpty(store) || !_storage.FileExists(store))
        {
            FileSystemException.ThrowFileNotFound(absolute);
        }

        try
        {
            _storage.DeleteFile(store);
        }
        catch (FileNotFoundException ex)
        {
            FileSystemException.ThrowFileNotFound(absolute, ex);
        }
        catch (UnauthorizedAccessException ex)
        {
            FileSystemException.ThrowAccessDenied(absolute, ex);
        }
        catch (IsolatedStorageException ex)
        {
            FileSystemException.ThrowAccessDenied(absolute, ex);
        }
    }

    /// <inheritdoc />
    public IFileSystemInfo GetInfo(FileSystemPath path)
    {
        CheckIfDisposed();
        FileSystemPath absolute = IsolatedPathHelper.ToAbsolute(path);
        string store = IsolatedPathHelper.ToStorePath(absolute);

        if (string.IsNullOrEmpty(store))
        {
            return _root;
        }

        if (_storage.DirectoryExists(store))
        {
            return new IsolatedFileSystemDirectory(this, _storage, absolute);
        }

        if (_storage.FileExists(store))
        {
            return new IsolatedFileSystemFile(this, _storage, absolute);
        }

        FileSystemException.ThrowPathNotFound(absolute);
        return default!; // unreachable
    }

    /// <inheritdoc />
    public IFileSystemDirectory GetDirectory(FileSystemPath path)
    {
        CheckIfDisposed();
        FileSystemPath absolute = IsolatedPathHelper.ToAbsolute(path);
        string store = IsolatedPathHelper.ToStorePath(absolute);

        if (string.IsNullOrEmpty(store))
        {
            return _root;
        }

        if (!_storage.DirectoryExists(store))
        {
            FileSystemException.ThrowDirectoryNotFound(absolute);
        }

        return new IsolatedFileSystemDirectory(this, _storage, absolute);
    }

    /// <inheritdoc />
    public IFileSystemFile GetFile(FileSystemPath path)
    {
        CheckIfDisposed();
        FileSystemPath absolute = IsolatedPathHelper.ToAbsolute(path);
        string store = IsolatedPathHelper.ToStorePath(absolute);

        if (string.IsNullOrEmpty(store) || !_storage.FileExists(store))
        {
            FileSystemException.ThrowFileNotFound(absolute);
        }

        return new IsolatedFileSystemFile(this, _storage, absolute);
    }

    /// <inheritdoc />
    public void CopyFile(FileSystemPath source, FileSystemPath destination)
    {
        CheckIfDisposed();
        CheckIfReadOnly(nameof(CopyFile));

        FileSystemPath sourceAbs = IsolatedPathHelper.ToAbsolute(source);
        FileSystemPath destAbs = IsolatedPathHelper.ToAbsolute(destination);
        string sourceStore = IsolatedPathHelper.ToStorePath(sourceAbs);
        string destStore = IsolatedPathHelper.ToStorePath(destAbs);

        if (string.IsNullOrEmpty(sourceStore) || !_storage.FileExists(sourceStore))
        {
            FileSystemException.ThrowFileNotFound(sourceAbs);
        }

        if (_storage.FileExists(destStore) || _storage.DirectoryExists(destStore))
        {
            FileSystemException.ThrowPathAlreadyExist(destAbs);
        }

        EnsureParentDirectory(destStore);

        try
        {
            _storage.CopyFile(sourceStore, destStore);
        }
        catch (FileNotFoundException ex)
        {
            FileSystemException.ThrowFileNotFound(sourceAbs, ex);
        }
        catch (DirectoryNotFoundException ex)
        {
            FileSystemException.ThrowDirectoryNotFound(destAbs, ex);
        }
        catch (UnauthorizedAccessException ex)
        {
            FileSystemException.ThrowAccessDenied(sourceAbs, ex);
        }
        catch (IsolatedStorageException ex)
        {
            FileSystemException.ThrowAccessDenied(sourceAbs, ex);
        }
    }

    /// <inheritdoc />
    public void Move(FileSystemPath source, FileSystemPath destination)
    {
        CheckIfDisposed();
        CheckIfReadOnly(nameof(Move));

        FileSystemPath sourceAbs = IsolatedPathHelper.ToAbsolute(source);
        FileSystemPath destAbs = IsolatedPathHelper.ToAbsolute(destination);
        string sourceStore = IsolatedPathHelper.ToStorePath(sourceAbs);
        string destStore = IsolatedPathHelper.ToStorePath(destAbs);

        if (string.IsNullOrEmpty(sourceStore))
        {
            FileSystemException.ThrowPathNotFound(sourceAbs);
        }

        try
        {
            if (_storage.FileExists(sourceStore))
            {
                EnsureParentDirectory(destStore);
                _storage.MoveFile(sourceStore, destStore);
            }
            else if (_storage.DirectoryExists(sourceStore))
            {
                EnsureParentDirectory(destStore);
                _storage.MoveDirectory(sourceStore, destStore);
            }
            else
            {
                FileSystemException.ThrowPathNotFound(sourceAbs);
            }
        }
        catch (FileNotFoundException ex)
        {
            FileSystemException.ThrowFileNotFound(sourceAbs, ex);
        }
        catch (DirectoryNotFoundException ex)
        {
            FileSystemException.ThrowDirectoryNotFound(sourceAbs, ex);
        }
        catch (UnauthorizedAccessException ex)
        {
            FileSystemException.ThrowAccessDenied(sourceAbs, ex);
        }
        catch (IsolatedStorageException ex)
        {
            FileSystemException.ThrowAccessDenied(sourceAbs, ex);
        }
    }

    /// <inheritdoc />
    public IFileSystemEventToken Watch(Glob? pattern)
    {
        CheckIfDisposed();
        // IsolatedStorageFile does not surface change notifications; the noop token documents
        // the limitation while keeping the caller pattern uniform with other providers.
        return IsolatedFileSystemNoopEventToken.Instance;
    }

    /// <inheritdoc />
    public IEnumerable<IFileSystemInfo> EnumerateFileSystem(FileSystemEnumerationOptions? options = null)
    {
        CheckIfDisposed();
        return _root.EnumerateFileSystem(options);
    }

    /// <inheritdoc />
    public IEnumerator<IFileSystemInfo> GetEnumerator()
        => EnumerateFileSystem(new FileSystemEnumerationOptions { Recurse = true }).GetEnumerator();

    IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();

    /// <inheritdoc />
    public void Dispose()
    {
        if (_isDisposed)
        {
            return;
        }

        _isDisposed = true;

        try
        {
            if (_removeStoreOnDispose)
            {
                _storage.Remove();
            }
        }
        catch (IsolatedStorageException)
        {
            // Best-effort cleanup; the store may already be removed or held by another process.
        }
        finally
        {
            _storage.Dispose();
        }
    }

    /// <inheritdoc />
    public ValueTask DisposeAsync()
    {
        Dispose();
        return ValueTask.CompletedTask;
    }

    /// <summary>
    /// Acquires the backing <see cref="IsolatedStorageFile"/> instance from the scope and
    /// evidence types declared on <paramref name="options"/>. Mirrors the overload selection
    /// rules baked into <see cref="IsolatedStorageFile.GetStore(IsolatedStorageScope, Type, Type)"/>.
    /// </summary>
    private static IsolatedStorageFile OpenStore(IsolatedFileSystemOptions options)
    {
        bool hasApplication = (options.Scope & IsolatedStorageScope.Application) != 0;
        bool hasDomain = (options.Scope & IsolatedStorageScope.Domain) != 0;

        if (hasApplication)
        {
            return IsolatedStorageFile.GetStore(options.Scope, options.ApplicationEvidenceType);
        }

        if (hasDomain)
        {
            return IsolatedStorageFile.GetStore(
                options.Scope,
                options.DomainEvidenceType,
                options.AssemblyEvidenceType);
        }

        // Default — Assembly (or User+Assembly).
        return IsolatedStorageFile.GetStore(options.Scope, options.AssemblyEvidenceType);
    }

    /// <summary>
    /// Ensures every intermediate directory in <paramref name="storePath"/> exists. The isolated
    /// store treats <c>CreateFile</c> on a path with missing parents as a hard failure, so this
    /// brings the provider into parity with InMemory + Physical (which auto-create parents).
    /// </summary>
    private void EnsureParentDirectory(string storePath)
    {
        int lastSep = storePath.LastIndexOf('/');
        if (lastSep <= 0)
        {
            return;
        }

        string parent = storePath.Substring(0, lastSep);
        if (!_storage.DirectoryExists(parent))
        {
            _storage.CreateDirectory(parent);
        }
    }

    /// <summary>
    /// Recursively removes <paramref name="storePath"/>. <see cref="IsolatedStorageFile.DeleteDirectory"/>
    /// requires the directory to be empty, so we walk children first.
    /// </summary>
    private void DeleteDirectoryRecursive(string storePath)
    {
        string searchRoot = string.IsNullOrEmpty(storePath) ? "*" : storePath + "/*";

        foreach (var fileName in _storage.GetFileNames(searchRoot))
        {
            string filePath = string.IsNullOrEmpty(storePath) ? fileName : storePath + "/" + fileName;
            _storage.DeleteFile(filePath);
        }

        foreach (var dirName in _storage.GetDirectoryNames(searchRoot))
        {
            string dirPath = string.IsNullOrEmpty(storePath) ? dirName : storePath + "/" + dirName;
            DeleteDirectoryRecursive(dirPath);
        }

        if (!string.IsNullOrEmpty(storePath))
        {
            _storage.DeleteDirectory(storePath);
        }
    }

    private void CheckIfReadOnly(string operation)
    {
        if (_isReadOnly)
        {
            FileSystemException.ThrowReadOnly(operation);
        }
    }

    private void CheckIfDisposed()
    {
        ObjectDisposedException.ThrowIf(_isDisposed, this);
    }
}

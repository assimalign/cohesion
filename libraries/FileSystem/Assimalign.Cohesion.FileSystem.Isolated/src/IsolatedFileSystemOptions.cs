using System;
using System.IO;
using System.IO.IsolatedStorage;
using System.Threading;

namespace Assimalign.Cohesion.FileSystem;

/// <summary>
/// Configuration for an <see cref="IsolatedFileSystem"/>. Controls which
/// <see cref="IsolatedStorageFile"/> store backs the file system, whether write operations are
/// rejected, and how the provider reports its display name and ignored attributes.
/// </summary>
public class IsolatedFileSystemOptions
{
    /// <summary>
    /// Display name reported through <see cref="IFileSystem.Name"/>. Defaults to
    /// <c>"IsolatedFileSystem"</c> when unset.
    /// </summary>
    public string? Name { get; set; }

    /// <summary>
    /// When <see langword="true"/> every mutating operation
    /// (<see cref="IFileSystem.CreateFile"/>, <see cref="IFileSystem.DeleteFile"/>, etc.) throws
    /// a <see cref="FileSystemException"/> with code
    /// <see cref="FileSystemErrorCode.ReadOnly"/>.
    /// </summary>
    public bool IsReadOnly { get; set; }

    /// <summary>
    /// Scope used to acquire the backing <see cref="IsolatedStorageFile"/>. Defaults to
    /// <c><see cref="IsolatedStorageScope.User"/> | <see cref="IsolatedStorageScope.Assembly"/></c>
    /// — the same store returned by <see cref="IsolatedStorageFile.GetUserStoreForAssembly"/>.
    /// </summary>
    public IsolatedStorageScope Scope { get; set; } = IsolatedStorageScope.User | IsolatedStorageScope.Assembly;

    /// <summary>
    /// Optional evidence type used to acquire the store when <see cref="Scope"/> includes
    /// <see cref="IsolatedStorageScope.Application"/>. Most callers leave this unset and rely on
    /// the default assembly evidence.
    /// </summary>
    public Type? ApplicationEvidenceType { get; set; }

    /// <summary>
    /// Optional evidence type used to acquire the store when <see cref="Scope"/> does not include
    /// <see cref="IsolatedStorageScope.Application"/>. Most callers leave this unset and rely on
    /// the default assembly evidence.
    /// </summary>
    public Type? AssemblyEvidenceType { get; set; }

    /// <summary>
    /// Optional evidence type used to acquire the store when <see cref="Scope"/> includes
    /// <see cref="IsolatedStorageScope.Domain"/>.
    /// </summary>
    public Type? DomainEvidenceType { get; set; }

    /// <summary>
    /// Attributes excluded from enumeration when callers do not supply their own
    /// <see cref="FileSystemEnumerationOptions"/>. Defaults to hidden + system, mirroring the
    /// physical and in-memory providers.
    /// </summary>
    public FileAttributes IgnoreAttributes { get; set; } = FileAttributes.Hidden | FileAttributes.System;

    /// <summary>
    /// When set to <see langword="true"/> the file system removes the backing
    /// <see cref="IsolatedStorageFile"/> store from disk on <see cref="IFileSystem.Dispose"/>
    /// (via <see cref="IsolatedStorageFile.Remove"/>). Useful for ephemeral / test stores.
    /// Defaults to <see langword="false"/> — disposal only releases the in-process handle.
    /// </summary>
    public bool RemoveStoreOnDispose { get; set; }

    /// <summary>
    /// Interval at which <see cref="IFileSystem.Watch"/> (and the directory / file overloads)
    /// poll the backing isolated store for changes. <see cref="IsolatedStorageFile"/> does not
    /// expose native change notifications, so the provider snapshots the directory tree on this
    /// cadence and dispatches <see cref="IFileSystemEventToken.OnCreate"/> /
    /// <see cref="IFileSystemEventToken.OnDelete"/> / <see cref="IFileSystemEventToken.OnChange"/>
    /// events based on the diff against the previous snapshot.
    /// <para>
    /// Defaults to 1 second. Set to <see cref="Timeout.InfiniteTimeSpan"/> (or any non-positive
    /// value) to disable polling — in that mode <see cref="IFileSystem.Watch"/> returns a noop
    /// token that never fires.
    /// </para>
    /// </summary>
    public TimeSpan WatchPollInterval { get; set; } = TimeSpan.FromSeconds(1);
}

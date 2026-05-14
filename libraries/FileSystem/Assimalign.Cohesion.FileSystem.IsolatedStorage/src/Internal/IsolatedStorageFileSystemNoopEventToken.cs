using System;

namespace Assimalign.Cohesion.FileSystem.Internal;

/// <summary>
/// <see cref="IFileSystemEventToken"/> stub returned by <see cref="IsolatedStorageFileSystem"/> for
/// watch calls. <see cref="System.IO.IsolatedStorage.IsolatedStorageFile"/> does not surface
/// change notifications, so this token registers no callbacks and never fires. Returning a noop
/// token keeps client code uniform with the other providers while documenting that the watch
/// capability is intentionally absent on the isolated store.
/// </summary>
internal sealed class IsolatedStorageFileSystemNoopEventToken : IFileSystemEventToken
{
    /// <summary>
    /// Shared singleton — no state is required because every callback registration is a no-op.
    /// </summary>
    public static readonly IsolatedStorageFileSystemNoopEventToken Instance = new();

    private static readonly IDisposable NoopDisposable = new NoopRegistration();

    private IsolatedStorageFileSystemNoopEventToken() { }

    /// <inheritdoc />
    public IDisposable OnChange(Action<object?> callback, object? state) => NoopDisposable;

    /// <inheritdoc />
    public IDisposable OnChange<T>(Action<FileSystemEvent<T?>> callback, T? state) => NoopDisposable;

    /// <inheritdoc />
    public IDisposable OnCreate<T>(Action<FileSystemEvent<T?>> callback, T? state) => NoopDisposable;

    /// <inheritdoc />
    public IDisposable OnDelete<T>(Action<FileSystemEvent<T?>> callback, T? state) => NoopDisposable;

    /// <inheritdoc />
    public IDisposable OnRename<T>(Action<FileSystemRenameEvent<T?>> callback, T? state) => NoopDisposable;

    private sealed class NoopRegistration : IDisposable
    {
        public void Dispose() { }
    }
}

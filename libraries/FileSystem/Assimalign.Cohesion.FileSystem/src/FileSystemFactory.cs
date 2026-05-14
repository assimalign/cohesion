using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace Assimalign.Cohesion.FileSystem;

/// <summary>
/// Default <see cref="IFileSystemFactory"/>. Returns registered file systems by name (lazily
/// materializing the underlying instance on first access), and owns their lifecycle: disposing
/// the factory disposes every realized file system.
/// </summary>
/// <remarks>
/// Build the factory through <see cref="FileSystemFactoryBuilder"/>. The factory is
/// thread-safe; <see cref="Create(string)"/> is safe to call concurrently and the underlying
/// instance is materialized at most once per registration.
/// </remarks>
public sealed class FileSystemFactory : IFileSystemFactory
{
    private readonly Dictionary<string, Func<IFileSystem>> _registrations;
    private readonly Dictionary<string, IFileSystem> _materialized;
    private readonly Lock _gate = new();
    private int _disposed;

    internal FileSystemFactory(Dictionary<string, Func<IFileSystem>> registrations)
    {
        _registrations = registrations;
        _materialized = new Dictionary<string, IFileSystem>(StringComparer.OrdinalIgnoreCase);
    }

    /// <inheritdoc />
    public IReadOnlyCollection<string> Names => _registrations.Keys;

    /// <inheritdoc />
    public IFileSystem Create(string name)
    {
        ArgumentException.ThrowIfNullOrEmpty(name);
        ThrowIfDisposed();

        return Resolve(name);
    }

    /// <inheritdoc />
    public IFileSystem Create<TFileSystem>() where TFileSystem : IFileSystem
    {
        ThrowIfDisposed();

        // Walk the registrations and look for the first one whose materialized instance is
        // assignable to TFileSystem. Materializing on demand here matches the lazy semantics of
        // the name-based path.
        foreach (var name in _registrations.Keys)
        {
            var instance = Resolve(name);
            if (instance is TFileSystem)
            {
                return instance;
            }
        }

        var message = $"No file system of type '{typeof(TFileSystem).FullName}' is registered.";
        throw new FileSystemException(FileSystemErrorCode.NotFound, message);
    }

    /// <inheritdoc />
    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        IFileSystem[] toDispose;
        lock (_gate)
        {
            toDispose = new IFileSystem[_materialized.Count];
            _materialized.Values.CopyTo(toDispose, 0);
            _materialized.Clear();
        }

        foreach (var fileSystem in toDispose)
        {
            try
            {
                fileSystem.Dispose();
            }
            catch
            {
                // File system disposal failures must not abort teardown of the remaining sinks.
            }
        }
    }

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        IFileSystem[] toDispose;
        lock (_gate)
        {
            toDispose = new IFileSystem[_materialized.Count];
            _materialized.Values.CopyTo(toDispose, 0);
            _materialized.Clear();
        }

        foreach (var fileSystem in toDispose)
        {
            try
            {
                await fileSystem.DisposeAsync().ConfigureAwait(false);
            }
            catch
            {
                // File system disposal failures must not abort teardown of the remaining sinks.
            }
        }
    }

    private IFileSystem Resolve(string name)
    {
        lock (_gate)
        {
            if (_materialized.TryGetValue(name, out var existing))
            {
                return existing;
            }

            if (!_registrations.TryGetValue(name, out var factory))
            {
                throw new FileSystemException(
                    FileSystemErrorCode.NotFound,
                    $"No file system is registered under the name '{name}'.");
            }

            var instance = factory.Invoke();
            if (instance is null)
            {
                throw new FileSystemException(
                    FileSystemErrorCode.Other,
                    $"The factory for file system '{name}' returned null.");
            }

            _materialized.Add(name, instance);
            return instance;
        }
    }

    private void ThrowIfDisposed()
    {
        if (Volatile.Read(ref _disposed) != 0)
        {
            throw new ObjectDisposedException(nameof(FileSystemFactory));
        }
    }
}

using System;
using System.Collections.Generic;

namespace Assimalign.Cohesion.FileSystem;

/// <summary>
/// Fluent registration surface for <see cref="IFileSystemFactory"/>. Build the factory in a
/// single thread, then publish the resulting (thread-safe) factory.
/// </summary>
/// <remarks>
/// The builder is single-use: after <see cref="Build"/> returns, subsequent calls throw
/// <see cref="InvalidOperationException"/>.
/// </remarks>
public sealed class FileSystemFactoryBuilder
{
    private readonly Dictionary<string, Func<IFileSystem>> _registrations =
        new(StringComparer.OrdinalIgnoreCase);
    private bool _built;

    /// <summary>
    /// Registers an already-instantiated file system under <paramref name="name"/>.
    /// </summary>
    /// <param name="name">Case-insensitive registration name. Required, non-empty, unique.</param>
    /// <param name="fileSystem">The file system instance to register. Required.</param>
    /// <exception cref="ArgumentException"><paramref name="name"/> is null or empty.</exception>
    /// <exception cref="ArgumentNullException"><paramref name="fileSystem"/> is <see langword="null"/>.</exception>
    /// <exception cref="InvalidOperationException">A file system is already registered under <paramref name="name"/>, or the builder has already been used to <see cref="Build"/>.</exception>
    public FileSystemFactoryBuilder AddFileSystem(string name, IFileSystem fileSystem)
    {
        ArgumentException.ThrowIfNullOrEmpty(name);
        ArgumentNullException.ThrowIfNull(fileSystem);
        ThrowIfBuilt();

        Register(name, () => fileSystem);
        return this;
    }

    /// <summary>
    /// Registers a deferred factory under <paramref name="name"/>. The factory is invoked lazily
    /// on first <see cref="IFileSystemFactory.Create(string)"/> access.
    /// </summary>
    /// <param name="name">Case-insensitive registration name. Required, non-empty, unique.</param>
    /// <param name="factory">Factory delegate that returns a new file system instance.</param>
    /// <exception cref="ArgumentException"><paramref name="name"/> is null or empty.</exception>
    /// <exception cref="ArgumentNullException"><paramref name="factory"/> is <see langword="null"/>.</exception>
    /// <exception cref="InvalidOperationException">A file system is already registered under <paramref name="name"/>, or the builder has already been used to <see cref="Build"/>.</exception>
    public FileSystemFactoryBuilder AddFileSystem<TFileSystem>(string name, Func<TFileSystem> factory)
        where TFileSystem : IFileSystem
    {
        ArgumentException.ThrowIfNullOrEmpty(name);
        ArgumentNullException.ThrowIfNull(factory);
        ThrowIfBuilt();

        Register(name, () => factory.Invoke());
        return this;
    }

    /// <summary>
    /// Convenience overload that uses <c>typeof(TFileSystem).Name</c> as the registration name.
    /// Preserves backward compatibility with provider extension methods that rely on the type
    /// name convention.
    /// </summary>
    /// <exception cref="ArgumentNullException"><paramref name="factory"/> is <see langword="null"/>.</exception>
    /// <exception cref="InvalidOperationException">A file system is already registered under the type's name, or the builder has already been used to <see cref="Build"/>.</exception>
    public FileSystemFactoryBuilder AddFileSystem<TFileSystem>(Func<TFileSystem> factory)
        where TFileSystem : IFileSystem
    {
        ArgumentNullException.ThrowIfNull(factory);
        return AddFileSystem(typeof(TFileSystem).Name, factory);
    }

    /// <summary>
    /// Materializes the factory. The builder cannot be reused after this call.
    /// </summary>
    /// <exception cref="InvalidOperationException">The builder has already been used to <see cref="Build"/>.</exception>
    public IFileSystemFactory Build()
    {
        ThrowIfBuilt();
        _built = true;

        var snapshot = new Dictionary<string, Func<IFileSystem>>(_registrations, StringComparer.OrdinalIgnoreCase);
        return new FileSystemFactory(snapshot);
    }

    private void Register(string name, Func<IFileSystem> factory)
    {
        if (_registrations.ContainsKey(name))
        {
            throw new InvalidOperationException(
                $"A file system named '{name}' is already registered.");
        }

        _registrations.Add(name, factory);
    }

    private void ThrowIfBuilt()
    {
        if (_built)
        {
            throw new InvalidOperationException(
                "FileSystemFactoryBuilder has already been used to build a factory; create a new builder.");
        }
    }
}

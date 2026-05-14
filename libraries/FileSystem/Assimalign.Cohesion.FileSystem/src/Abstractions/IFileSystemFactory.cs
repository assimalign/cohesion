using System;
using System.Collections.Generic;

namespace Assimalign.Cohesion.FileSystem;

/// <summary>
/// Returns named <see cref="IFileSystem"/> instances. The factory owns its registered file
/// systems' lifecycle: disposing the factory disposes every file system it was built with.
/// </summary>
public interface IFileSystemFactory : IDisposable, IAsyncDisposable
{
    /// <summary>
    /// The names of the file systems registered with the factory.
    /// </summary>
    IReadOnlyCollection<string> Names { get; }

    /// <summary>
    /// Returns the registered file system bound to <paramref name="name"/>.
    /// </summary>
    /// <param name="name">The registration name supplied at build time. Required, non-empty.</param>
    /// <exception cref="ArgumentException"><paramref name="name"/> is null or empty.</exception>
    /// <exception cref="FileSystemException">No file system is registered under <paramref name="name"/> (code <see cref="FileSystemErrorCode.NotFound"/>).</exception>
    /// <exception cref="ObjectDisposedException">The factory has been disposed.</exception>
    IFileSystem Create(string name);

    /// <summary>
    /// Returns the first registered file system that is assignable to <typeparamref name="TFileSystem"/>.
    /// </summary>
    /// <exception cref="FileSystemException">No file system of the requested type is registered (code <see cref="FileSystemErrorCode.NotFound"/>).</exception>
    /// <exception cref="ObjectDisposedException">The factory has been disposed.</exception>
    IFileSystem Create<TFileSystem>() where TFileSystem : IFileSystem;
}

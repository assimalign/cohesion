using System;

namespace Assimalign.Cohesion.Security.DataProtection;

/// <summary>
/// Factory for the built-in <see cref="IKeyRepository"/> implementations.
/// </summary>
public static class KeyRepository
{
    /// <summary>
    /// Creates a file-system-backed key repository rooted at <paramref name="directoryPath"/>.
    /// The directory is created if it does not exist. For a multi-node deployment, point every
    /// node at the same shared directory.
    /// </summary>
    /// <param name="directoryPath">The directory that will hold one file per key.</param>
    /// <returns>A file-system-backed key repository.</returns>
    /// <exception cref="ArgumentException"><paramref name="directoryPath"/> is <see langword="null"/> or empty.</exception>
    public static IKeyRepository CreateFileSystem(string directoryPath)
    {
        if (string.IsNullOrEmpty(directoryPath))
        {
            throw new ArgumentException("A key repository directory path is required.", nameof(directoryPath));
        }

        return new FileSystemKeyRepository(directoryPath);
    }
}

using System;

namespace Assimalign.Cohesion.FileSystem;

/// <summary>
/// Convenience extensions for registering <see cref="IsolatedStorageFileSystem"/> instances on a
/// <see cref="FileSystemFactoryBuilder"/>.
/// </summary>
public static class IsolatedStorageFileSystemExtensions
{
    extension(FileSystemFactoryBuilder builder)
    {
        /// <summary>
        /// Registers an <see cref="IsolatedStorageFileSystem"/> with default options
        /// (user + assembly scope, not read-only, store retained on dispose).
        /// </summary>
        public FileSystemFactoryBuilder AddIsolatedStorageFileSystem()
            => builder.AddIsolatedStorageFileSystem(_ => { });

        /// <summary>
        /// Registers an <see cref="IsolatedStorageFileSystem"/> configured via
        /// <paramref name="configure"/>. The factory is invoked lazily on first
        /// <see cref="IFileSystemFactory.Create(string)"/>.
        /// </summary>
        /// <param name="configure">Callback used to populate the options instance.</param>
        public FileSystemFactoryBuilder AddIsolatedStorageFileSystem(Action<IsolatedStorageFileSystemOptions> configure)
        {
            ArgumentNullException.ThrowIfNull(configure);

            return builder.AddFileSystem<IsolatedStorageFileSystem>(() =>
            {
                var options = new IsolatedStorageFileSystemOptions();
                configure(options);
                return new IsolatedStorageFileSystem(options);
            });
        }
    }
}

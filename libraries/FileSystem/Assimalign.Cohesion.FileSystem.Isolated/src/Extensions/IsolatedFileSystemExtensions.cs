using System;

namespace Assimalign.Cohesion.FileSystem;

/// <summary>
/// Convenience extensions for registering <see cref="IsolatedFileSystem"/> instances on a
/// <see cref="FileSystemFactoryBuilder"/>.
/// </summary>
public static class IsolatedFileSystemExtensions
{
    extension(FileSystemFactoryBuilder builder)
    {
        /// <summary>
        /// Registers an <see cref="IsolatedFileSystem"/> with default options
        /// (user + assembly scope, not read-only, store retained on dispose).
        /// </summary>
        public FileSystemFactoryBuilder AddIsolatedFileSystem()
            => builder.AddIsolatedFileSystem(_ => { });

        /// <summary>
        /// Registers an <see cref="IsolatedFileSystem"/> configured via
        /// <paramref name="configure"/>. The factory is invoked lazily on first
        /// <see cref="IFileSystemFactory.Create(string)"/>.
        /// </summary>
        /// <param name="configure">Callback used to populate the options instance.</param>
        public FileSystemFactoryBuilder AddIsolatedFileSystem(Action<IsolatedFileSystemOptions> configure)
        {
            ArgumentNullException.ThrowIfNull(configure);

            return builder.AddFileSystem<IsolatedFileSystem>(() =>
            {
                var options = new IsolatedFileSystemOptions();
                configure(options);
                return new IsolatedFileSystem(options);
            });
        }
    }
}

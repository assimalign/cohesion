using System;

namespace Assimalign.Cohesion.FileSystem;

/// <summary>
/// Registration extensions that let callers add an <see cref="AggregateFileSystem"/> to a
/// <see cref="FileSystemFactoryBuilder"/> in the same fluent style as the other providers.
/// </summary>
public static class AggregateFileSystemExtensions
{
    extension(FileSystemFactoryBuilder builder)
    {
        /// <summary>
        /// Registers an <see cref="AggregateFileSystem"/> configured via
        /// <paramref name="configure"/>. The aggregate is materialized lazily on the first
        /// <see cref="IFileSystemFactory.Create(string)"/> call.
        /// </summary>
        /// <param name="configure">Callback that populates the aggregate's builder.</param>
        public FileSystemFactoryBuilder AddAggregateFileSystem(Action<AggregateFileSystemBuilder> configure)
        {
            ArgumentNullException.ThrowIfNull(configure);

            return builder.AddFileSystem<AggregateFileSystem>(() =>
            {
                var aggregateBuilder = new AggregateFileSystemBuilder();
                configure(aggregateBuilder);
                return aggregateBuilder.Build();
            });
        }
    }
}

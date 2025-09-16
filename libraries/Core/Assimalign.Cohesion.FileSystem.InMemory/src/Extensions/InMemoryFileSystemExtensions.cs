
using System;
using System.Collections.Generic;
using System.Text;

namespace Assimalign.Cohesion.FileSystem;

using Assimalign.Cohesion.Internal;

public static class InMemoryFileSystemExtensions
{
    extension(FileSystemFactoryBuilder builder)
    {
        /// <summary>
        /// Adds the physical file system to the factory builder.
        /// </summary>
        /// <param name="builder"></param>
        /// <returns></returns>
        public FileSystemFactoryBuilder AddInMemoryFileSystem()
        {
            return builder.AddInMemoryFileSystem(options =>
            {

            });
        }

        /// <summary>
        /// 
        /// </summary>
        /// <param name="configure"></param>
        /// <returns></returns>
        public FileSystemFactoryBuilder AddInMemoryFileSystem(Action<InMemoryFileSystemOptions> configure)
        {
            ThrowHelper.ThrowIfNull(configure);

            return builder.AddFileSystem<InMemoryFileSystem>(() =>
            {
                var options = new InMemoryFileSystemOptions();

                configure.Invoke(options);

                return new InMemoryFileSystem(options);
            });
        }
    }
}

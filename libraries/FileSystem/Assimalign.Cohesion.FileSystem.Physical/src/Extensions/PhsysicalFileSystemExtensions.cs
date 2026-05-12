using System;
using System.Collections.Generic;
using System.Text;

namespace Assimalign.Cohesion.FileSystem;

public static class PhsysicalFileSystemExtensions
{
    extension(FileSystemFactoryBuilder builder)
    {
        /// <summary>
        /// Adds the physical file system to the factory builder.
        /// </summary>
        /// <param name="builder"></param>
        /// <returns></returns>
        public FileSystemFactoryBuilder AddPhysicalFileSystem()
        {
            return builder.AddFileSystem<PhysicalFileSystem>(() => new PhysicalFileSystem(PhysicalFileSystemOptions.Default));
        }

        /// <summary>
        /// 
        /// </summary>
        /// <param name="configure"></param>
        /// <returns></returns>
        public FileSystemFactoryBuilder AddPhysicalFileSystem(Action<PhysicalFileSystemOptions> configure)
        {
            ArgumentNullException.ThrowIfNull(configure);

            return builder.AddFileSystem<PhysicalFileSystem>(() =>
            {
                var options = new PhysicalFileSystemOptions();

                configure.Invoke(options);

                return new PhysicalFileSystem(options);
            });
        }
    }
}
